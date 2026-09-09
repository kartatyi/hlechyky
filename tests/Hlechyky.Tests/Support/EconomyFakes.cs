using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Tests.Support;

/// <summary>Поштова скринька, яка нічого не розсилає, а просто пам'ятає, що їй дали.</summary>
public sealed class FakeOutbox : IOutbox
{
    readonly List<Outgoing> _messages = new();

    public void Post(Outgoing message)
    {
        lock (_messages) _messages.Add(message);
    }

    public List<Outgoing> All
    {
        get { lock (_messages) return new List<Outgoing>(_messages); }
    }

    public List<T> Of<T>() where T : Outgoing => All.OfType<T>().ToList();

    public void Clear()
    {
        lock (_messages) _messages.Clear();
    }
}

/// <summary>Налаштування, які не міняються: IOptionsMonitor без усієї машинерії конфігурації.</summary>
public sealed class FixedOptions<T>(T value) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue { get; set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// Зібрана економіка на тимчасовій базі: усе, що потрібно тестам, уже зв'язане між собою.
/// <see cref="Rewards"/> одразу підписаний на <see cref="Events"/>, тож тест може просто підняти подію.
/// </summary>
public sealed class EconomyRig : IDisposable
{
    public TempDb Temp { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeOutbox Outbox { get; } = new();
    public EconomyOptions Options { get; } = new();
    public GameEvents Events { get; } = new();
    public Presence Presence { get; } = new();

    public Db Db => Temp.Db;
    public EconomyStore Store { get; }
    public GameNames Names { get; }
    public Economy Economy { get; }
    public Ratings Ratings { get; }
    public Achievements Achievements { get; }
    public Daily Daily { get; }
    public Leaderboards Boards { get; }
    public Rewards Rewards { get; }
    public EconomyTicker Ticker { get; }
    public SqliteGameStore GameStore { get; }

    public EconomyRig()
    {
        var opts = new FixedOptions<EconomyOptions>(Options);
        Store = new EconomyStore(Db);
        Names = new GameNames();
        Economy = new Economy(Store, Names, Clock, opts, Outbox);
        Ratings = new Ratings(Store, Clock);
        Achievements = new Achievements(Store, Economy, Names, Clock, Outbox);
        Daily = new Daily(Store, Names, Clock);
        Boards = new Leaderboards(Store, Ratings, Achievements, Daily, Names, Economy, Clock);
        Rewards = new Rewards(Events, Economy, Store, Ratings, Achievements, Daily, Names, Clock, opts,
            NullLogger<Rewards>.Instance);
        Rewards.Attach();
        Ticker = new EconomyTicker(Presence, Economy, Store, Achievements, Clock, opts, NullLogger<EconomyTicker>.Instance);
        GameStore = new SqliteGameStore(Db, Clock);
    }

    /// <summary>
    /// Скільки черепків нік дістав саме за цю причину. Баланс перевіряти незручно: поверх нагороди за
    /// партію одразу лягають ачівки, і тест починає рахувати не те, що перевіряє.
    /// </summary>
    public int Paid(string nick, string reasonPrefix) => Outbox.Of<WalletChanged>()
        .Where(w => Economy.Key(w.Nick) == Economy.Key(nick)
                    && w.Reason.StartsWith(reasonPrefix, StringComparison.Ordinal))
        .Sum(w => w.Delta);

    /// <summary>Паспорт «гри», якої в збірці ще нема: тести на таблиці й нагороди мають із чим працювати.</summary>
    public static GameInfo Info(string id, string title, string accusative, GameGroup group = GameGroup.Board,
        int max = 2, bool rated = false, ScoreOrder score = ScoreOrder.None) =>
        new(id, title, accusative, group, group == GameGroup.Solo ? 1 : 2, max, Rated: rated, Score: score);

    /// <summary>Подія «партія скінчилась» із розумними значеннями за замовчуванням (довга партія, без ставки).</summary>
    public RoomFinishedEvent Finished(string roomId, GameInfo info, string?[] seats, int[] winners,
        bool draw = false, int round = 1, int stake = 0, int moves = 20, double seconds = 120,
        IReadOnlyDictionary<int, long>? scores = null)
    {
        var started = Clock.UtcNow;
        return new RoomFinishedEvent(roomId, info.Id, info, round, seats,
            new RoomResult(winners, draw, "тест", scores), stake,
            started, started.AddSeconds(seconds), moves);
    }

    public void Dispose() => Temp.Dispose();
}
