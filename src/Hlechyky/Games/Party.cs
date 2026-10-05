using System.Reflection;
using System.Text.Json;
using Hlechyky.Games.Impl;
using Microsoft.Extensions.Logging;

namespace Hlechyky.Games;

// ============================================================================================
// Каркас міні-ігор «Глечикової вечірки» (хвиля «Вечірка й міні-ігри», 05.10.2026). Вечірка — настільна гра-дошка,
// між ходами якої всі за столом грають коротку міні-гру з нашого каталогу. Міні-гра живе всередині столу вечірки:
// вечірка створює екземпляр іншої гри, дає їй свій IRoomContext і перехоплює Finish. Контракт для авторів ігор —
// docs/games/specs/party-minigame.md.
// ============================================================================================

/// <summary>
/// Режим вечірки з опцій гри: <c>party=1</c>, <c>bots=0,3</c> (місця ботів), <c>botlvl=easy|normal|hard</c>.
/// У звичайних столах <c>Rooms.Effective</c> ключі, яких нема в <c>Info.Options</c>, відкидає — тож у лобі режим
/// не ввімкнеш: ці опції приходять лише від <see cref="MinigameHost"/>.
/// </summary>
public sealed record PartyMode(int[] Bots, LiveBots.Level Level)
{
    public const string Key = "party", BotsKey = "bots", LevelKey = "botlvl";

    /// <summary>Режим вечірки або null, якщо гра звичайна.</summary>
    public static PartyMode? Read(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue(Key, out var on) || on != "1") return null;
        var bots = options.TryGetValue(BotsKey, out var b) ? GameOption.Split(b)
            .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n is >= 0 and < 64).Distinct().Order().ToArray() : [];
        return new PartyMode(bots, LiveBots.Read(options));
    }

    public bool IsBot(int seat) => Array.IndexOf(Bots, seat) >= 0;

    /// <summary>Опції для Configure підгри: режим, боти, рівень і додаткові (напр. тема міні-гри).</summary>
    public static Dictionary<string, string> Options(IEnumerable<int> bots, LiveBots.Level level, IReadOnlyDictionary<string, string>? extra = null)
    {
        var o = extra is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(extra, StringComparer.Ordinal);
        o[Key] = "1";
        o[BotsKey] = string.Join(",", bots);
        o[LevelKey] = LiveBots.Key(level);
        return o;
    }
}

/// <summary>
/// Гра, яку можна взяти міні-грою вечірки. Лише ігри з цим інтерфейсом потрапляють у пул (<see cref="PartyPool"/>):
/// старі ігри туди випадково не прийдуть. У режимі вечірки (<see cref="PartyMode"/>) гра грає одну коротку партію
/// без лобі й готовності і кличе <c>Ctx.Finish</c> з повними scores для кожного місця.
/// </summary>
public interface IPartyMinigame
{
    /// <summary>1–2 рядки «що робити» й клавіші — вечірка покаже це карткою перед стартом.</summary>
    string Howto { get; }

    /// <summary>Жорстка стеля від Start до кінця в режимі вечірки, мс (хост бере не більше <see cref="MinigameHost.MaxCapMs"/>).</summary>
    int PartyCapMs { get; }

    /// <summary>Скільки місць гра тримає в режимі вечірки (вечірка — 2–8).</summary>
    int PartyMin { get; }
    int PartyMax { get; }

    /// <summary>
    /// Поточні scores кожного місця (більше = краще) — коли стеля минула, а гра не скінчилась. Кличеться під тим самим
    /// замком, що й Tick; стан гри після цього вже не потрібен.
    /// </summary>
    IReadOnlyDictionary<int, long> PartyScores();
}

/// <summary>Маркер: гри нема в каталозі лобі (стенди для розробки, як <c>mgprobe</c>) — стіл відкривають посиланням.</summary>
public interface IUnlistedGame;

/// <summary>Пул міні-ігор: усі ігри збірки сервера з <see cref="IPartyMinigame"/>.</summary>
public static class PartyPool
{
    static readonly Lazy<IReadOnlyList<(Type Type, GameInfo Info)>> All = new(() =>
    {
        var found = new List<(Type, GameInfo)>();
        foreach (var t in typeof(Game).Assembly.GetTypes())
        {
            if (t.IsAbstract || !t.IsClass || !typeof(Game).IsAssignableFrom(t) || !typeof(IPartyMinigame).IsAssignableFrom(t)) continue;
            if (t.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is null) continue;
            found.Add((t, ((Game)Activator.CreateInstance(t)!).Info));
        }
        return [.. found.OrderBy(g => g.Item2.Id, StringComparer.Ordinal)];
    });

    /// <summary>Паспорти міні-ігор за абеткою Id.</summary>
    public static IReadOnlyList<GameInfo> Games => [.. All.Value.Select(g => g.Info)];

    public static bool Has(string id) => All.Value.Any(g => g.Info.Id == id);

    /// <summary>Новий екземпляр міні-гри або null, якщо такої в пулі нема.</summary>
    public static Game? Create(string id)
    {
        foreach (var (type, info) in All.Value)
            if (info.Id == id) return (Game)Activator.CreateInstance(type)!;
        return null;
    }
}

/// <summary>Місце підгри: місце в батьківському столі (−1 — нема) і чи там бот.</summary>
public readonly record struct PartySeat(int Parent, bool Bot)
{
    public static PartySeat Human(int parent) => new(parent, false);
    public static PartySeat BotAt(int parent = -1) => new(parent, true);
}

/// <summary>Як скінчилась міні-гра: сама (Finish), стелею часу (PartyScores) чи винятком (усі рівні).</summary>
public enum MinigameEnd { Finished, Cap, Crash }

/// <summary>
/// Результат міні-гри в місцях підгри: scores кожного місця (більше = краще), місця (1 — найкраще, рівні — поділене),
/// переможці (усі з найкращим score). Log і Verdict — що гра сказала в Finish (у Журнал вони не йдуть).
/// </summary>
public sealed record MinigameResult(int[] Winners, IReadOnlyDictionary<int, long> Scores, int[] Places, string Log, string? Verdict, MinigameEnd How)
{
    /// <summary>Добудувати результат зі scores: бракує місця — 0 (контракт вимагає всі, але вечірка не має падати).</summary>
    public static MinigameResult From(int seats, IReadOnlyDictionary<int, long>? scores, string log, string? verdict, MinigameEnd how)
    {
        var s = new Dictionary<int, long>(seats);
        for (var i = 0; i < seats; i++) s[i] = scores is not null && scores.TryGetValue(i, out var v) ? v : 0;
        var places = new int[seats];
        for (var i = 0; i < seats; i++) places[i] = 1 + s.Values.Count(v => v > s[i]);
        var best = seats == 0 ? 0 : s.Values.Max();
        var winners = seats == 0 ? [] : s.Where(kv => kv.Value == best).Select(kv => kv.Key).Order().ToArray();
        return new MinigameResult(winners, s, places, log, verdict, how);
    }

    /// <summary>«Усі рівні» — міні-гра зламалась, вечірка живе далі.</summary>
    public static MinigameResult Even(int seats, string log) => From(seats, null, log, null, MinigameEnd.Crash);
}

/// <summary>
/// Контекст підгри. Rng — свій (засіяний з батьківського, тож детермінований сідом столу), Clock/Services — від
/// батька; місця — свої 0..N−1 за <see cref="PartySeat"/>; Finish перехоплюється й не доходить до кімнати;
/// Log/Say — у батьківський; Score/Award глушаться (лічильник — для тестів).
/// </summary>
public sealed class SubRoomContext : IRoomContext
{
    readonly IRoomContext _parent;
    readonly PartySeat[] _seats;
    readonly Random _rng;

    public SubRoomContext(IRoomContext parent, IReadOnlyList<PartySeat> seats, IReadOnlyDictionary<string, string> options)
    {
        _parent = parent;
        _seats = [.. seats];
        _rng = new Random(parent.Rng.Next());
        Options = options;
    }

    public string RoomId => _parent.RoomId;
    public int Players => _seats.Length;
    public int Round => 1;
    public Random Rng => _rng;
    public IClock Clock => _parent.Clock;
    public IReadOnlyDictionary<string, string> Options { get; }
    public IServiceProvider Services => _parent.Services;

    public IReadOnlyList<PartySeat> Seats => _seats;

    /// <summary>Місце підгри для місця батьківського столу (лише людське) або null.</summary>
    public int? SubOf(int parentSeat)
    {
        for (var i = 0; i < _seats.Length; i++) if (!_seats[i].Bot && _seats[i].Parent == parentSeat) return i;
        return null;
    }

    public string? NickOf(int seat) =>
        seat >= 0 && seat < _seats.Length && !_seats[seat].Bot && _seats[seat].Parent >= 0 ? _parent.NickOf(_seats[seat].Parent) : null;

    /// <summary>Лише присутні люди: бот і той, хто встав посеред міні-гри, — не «сидять».</summary>
    public bool Seated(int seat) =>
        seat >= 0 && seat < _seats.Length && !_seats[seat].Bot && _seats[seat].Parent >= 0 && _parent.Seated(_seats[seat].Parent);

    public int? HostSeat => _parent.HostSeat is { } h ? SubOf(h) : null;

    /// <summary>Що гра сказала в Finish; null — ще грає.</summary>
    public (int[] Winners, string Log, IReadOnlyDictionary<int, long>? Scores, string? Verdict)? Finished { get; private set; }

    /// <summary>Скільки Score/Award гра спробувала послати (у режимі вечірки їх бути не має — тест це ловить).</summary>
    public int Muted { get; private set; }

    public void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null, string? verdict = null)
    {
        if (Finished is not null) return;
        Finished = ([.. winners], log, scores, verdict);
    }

    public void Log(string text) => _parent.Log(text);
    public void Say(string text) => _parent.Say(text);
    public void Score(int seat, double value, int? attempts = null) => Muted++;
    public void Award(int seat, int shards, string reason) => Muted++;
}

/// <summary>
/// Хост міні-гри всередині батьківської гри (вечірка, mgprobe). Створює гру, Configure → Start, прокидає Act/Input,
/// тикає її з її власним кроком за годинником батька, стежить за стелею й винятками і віддає <see cref="Result"/>.
/// Усі методи кличуть під замком батьківської кімнати (з його Start/Act/Tick) — тоді Log/Say підгри доходять.
/// <para>Тики: батько тикає часто (вечірка — 20 мс, крок TickEngine), хост кличе <c>Tick()</c> підгри щоразу, коли
/// за <c>Ctx.Clock</c> настав її час, але не більше <see cref="MaxCatchUp"/> кроків за раз — пропущене далі не надолужує.</para>
/// <para>Save/Load: хост не зберігається. Батько після рестарту сервера стартує ту саму міні-гру заново (новий хост, той
/// самий id і склад): переграти чесніше, ніж зарахувати нічию, а партія коротка.</para>
/// </summary>
public sealed class MinigameHost
{
    /// <summary>Стеля будь-якої міні-гри, хоч би що казала гра.</summary>
    public const int MaxCapMs = 120_000;
    /// <summary>Скільки кроків підгри можна зробити за один тик батька (сервер підгальмував).</summary>
    public const int MaxCatchUp = 2;

    readonly Game _game;
    readonly IPartyMinigame _mini;
    readonly SubRoomContext _ctx;
    readonly int _step;
    DateTimeOffset _due;
    DateTimeOffset _deadline;
    bool _started;

    public MinigameHost(Game game, IRoomContext parent, IReadOnlyList<PartySeat> seats, LiveBots.Level level,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        _game = game;
        _mini = game as IPartyMinigame ?? throw new ArgumentException($"гра {game.Info.Id} не міні-гра вечірки", nameof(game));
        var bots = Enumerable.Range(0, seats.Count).Where(i => seats[i].Bot);
        _ctx = new SubRoomContext(parent, seats, PartyMode.Options(bots, level, extra));
        _game.Ctx = _ctx;
        _step = game.Info.TickMs;   // 0 — покрокова: Tick() їй не кличемо, лише стеля
    }

    /// <summary>Хост для гри з пулу за id; null — нема такої міні-гри.</summary>
    public static MinigameHost? Create(string id, IRoomContext parent, IReadOnlyList<PartySeat> seats, LiveBots.Level level,
        IReadOnlyDictionary<string, string>? extra = null) =>
        PartyPool.Create(id) is { } g ? new MinigameHost(g, parent, seats, level, extra) : null;

    public Game Game => _game;
    public IPartyMinigame Mini => _mini;
    public SubRoomContext Ctx => _ctx;
    public string Id => _game.Info.Id;
    public string Title => _game.Info.Title;
    public string Howto => _mini.Howto;
    public int Seats => _ctx.Players;
    public int CapMs => Math.Clamp(_mini.PartyCapMs, 1000, MaxCapMs);
    public bool Started => _started;
    public DateTimeOffset Deadline => _deadline;
    public MinigameResult? Result { get; private set; }
    public bool Over => Result is not null;

    /// <summary>Configure → Start. Виняток (і GameError з Configure) — результат «усі рівні».</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        var now = _ctx.Clock.UtcNow;
        _due = now.AddMilliseconds(Math.Max(1, _step));
        _deadline = now.AddMilliseconds(CapMs);
        if (!Guard(() => { _game.Configure(_ctx.Options); _game.Start(); })) return;
        Collect();
    }

    /// <summary>Хід (і Input) з місця батька. Не людина цієї міні-гри чи гра скінчилась — відмова, стан не міняється.</summary>
    public ActResult Act(int parentSeat, string action, JsonElement payload)
    {
        if (!_started || Over) return ActResult.Fail("Міні-гру вже зіграно");
        if (_ctx.SubOf(parentSeat) is not { } seat) return ActResult.Fail("Ти в цій міні-грі не граєш");
        ActResult r = ActResult.Fail("Тут так не ходять");
        try { r = _game.Act(seat, action, payload); }
        catch (GameError e) { return ActResult.Fail(e.Message); }
        catch (Exception e) { Crash(e); return ActResult.Fail("Міні-гра зламалась"); }
        Collect();
        return r;
    }

    /// <summary>Тик батька. Повертає те, що сказала підгра (або Both, коли міні-гра щойно скінчилась).</summary>
    public TickResult Tick()
    {
        if (!_started || Over) return TickResult.None;
        var now = _ctx.Clock.UtcNow;
        bool frame = false, view = false;
        for (var n = 0; n < MaxCatchUp && _step > 0 && now >= _due && !Over; n++)
        {
            _due = _due.AddMilliseconds(_step);
            var r = TickResult.None;
            if (!Guard(() => r = _game.Tick())) return TickResult.Both;
            frame |= r.Frame;
            view |= r.View;
            Collect();
        }
        if (_step > 0 && now >= _due) _due = now.AddMilliseconds(_step);   // відстали більше ніж на MaxCatchUp — не надолужуємо
        if (!Over && now >= _deadline) Cap();
        return Over ? TickResult.Both : new TickResult(frame, view);
    }

    /// <summary>Вид місця батька (null — глядач чи не грає в міні-грі: тоді вид глядача).</summary>
    public object? View(int? parentSeat)
    {
        if (!_started) return null;
        var seat = parentSeat is { } p ? _ctx.SubOf(p) : null;
        object? v = null;
        return Guard(() => v = _game.View(seat)) ? v : null;
    }

    /// <summary>Кадр підгри (Frame, а без нього — вид глядача, як робить каркас).</summary>
    public object? Frame()
    {
        if (!_started) return null;
        object? f = null;
        return Guard(() => f = _game.Frame() ?? _game.View(null)) ? f : null;
    }

    /// <summary>Місце підгри для місця батька (лише людина цієї міні-гри).</summary>
    public int? SubOf(int parentSeat) => _ctx.SubOf(parentSeat);

    /// <summary>Імена місць підгри: нік, бот гри (SeatBot) або «🤖 бот».</summary>
    public string[] Names()
    {
        var r = new string[Seats];
        for (var i = 0; i < r.Length; i++)
        {
            string? bot = null;
            try { bot = _game.SeatBot(i); } catch { /* ім'я — не привід ламати вечірку */ }
            r[i] = _ctx.NickOf(i) ?? bot ?? LiveBots.Name;
        }
        return r;
    }

    /// <summary>Назви місць гри («синій», «рудий») — для клієнтського ctx.seatName.</summary>
    public string[] SeatNames()
    {
        var r = new string[Seats];
        for (var i = 0; i < r.Length; i++)
        {
            try { r[i] = _game.SeatName(i); } catch { r[i] = $"гравець {i + 1}"; }
        }
        return r;
    }

    /// <summary>Стеля минула — scores гри як є (PartyScores), або «усі рівні», якщо й це впало.</summary>
    public void Cap()
    {
        if (Over) return;
        IReadOnlyDictionary<int, long>? s = null;
        if (!Guard(() => s = _mini.PartyScores())) return;
        Result = MinigameResult.From(Seats, s, $"{Title}: час вийшов", null, MinigameEnd.Cap);
    }

    void Collect()
    {
        if (Result is null && _ctx.Finished is { } f)
            Result = MinigameResult.From(Seats, f.Scores, f.Log, f.Verdict, MinigameEnd.Finished);
    }

    bool Guard(Action a)
    {
        try { a(); return true; }
        catch (Exception e) { Crash(e); return false; }
    }

    void Crash(Exception e)
    {
        (_ctx.Services?.GetService(typeof(ILogger<MinigameHost>)) as ILogger<MinigameHost>)?.LogError(e, "міні-гра {Game} у кімнаті {Room} впала", Id, _ctx.RoomId);
        Result ??= MinigameResult.Even(Seats, $"{Title}: міні-гра зламалась — усім порівну");
    }
}
