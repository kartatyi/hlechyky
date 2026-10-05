using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Support;

/// <summary>
/// Провести гру в режимі вечірки без вечірки: справжній <see cref="MinigameHost"/> над фальшивим батьківським столом.
/// Люди сидять на місцях 0..humans−1 (у батька — ті самі номери), боти — далі. <see cref="Tick"/> тикає, як вечірка:
/// крок батька 20 мс, а підгру хост тикає з її власним TickMs.
/// <code>
/// var h = new PartyHarness("tyr", humans: 1, bots: 3);
/// h.Start();
/// h.Act(0, "shoot", new { x = 10, y = 20 });
/// var r = h.RunToEnd();          // MinigameResult: Scores, Places, Winners, How
/// </code>
/// </summary>
public sealed class PartyHarness
{
    /// <summary>Крок батька — як у вечірки й mgprobe (TickEngine тикає раз на 20 мс).</summary>
    public const int ParentTickMs = 20;

    public PartyHarness(string gameId, int humans, int bots, LiveBots.Level level = LiveBots.Level.Normal, int seed = 42,
        IReadOnlyDictionary<string, string>? extra = null, IServiceProvider? services = null)
        : this(PartyPool.Create(gameId) ?? throw new ArgumentException($"«{gameId}» нема в пулі вечірки (IPartyMinigame)"),
            humans, bots, level, seed, extra, services) { }

    public PartyHarness(Game game, int humans, int bots, LiveBots.Level level = LiveBots.Level.Normal, int seed = 42,
        IReadOnlyDictionary<string, string>? extra = null, IServiceProvider? services = null)
        : this(game, [.. Enumerable.Range(0, humans).Select(PartySeat.Human), .. Enumerable.Range(0, bots).Select(_ => PartySeat.BotAt())],
            humans, level, seed, extra, services) { }

    /// <summary>Довільна розсадка (напр. бот посередині). Людські місця батька — 0..humans−1.</summary>
    public PartyHarness(Game game, IReadOnlyList<PartySeat> seats, int humans, LiveBots.Level level = LiveBots.Level.Normal, int seed = 42,
        IReadOnlyDictionary<string, string>? extra = null, IServiceProvider? services = null)
    {
        Parent = new ParentContext(Clock, seed, humans, services ?? new ServiceCollection().BuildServiceProvider());
        Game = game;
        Host = new MinigameHost(game, Parent, seats, level, extra);
    }

    public FakeClock Clock { get; } = new();
    public ParentContext Parent { get; }
    public MinigameHost Host { get; }
    public Game Game { get; }
    public MinigameResult? Result => Host.Result;
    public SubRoomContext Ctx => Host.Ctx;

    public void Start() { StartedAt = Clock.UtcNow; Host.Start(); }

    /// <summary>Хід людини з місця підгри (людське місце = місце батька).</summary>
    public ActResult Act(int seat, string action, object? payload = null) => Host.Act(Parent.ParentOf(Host, seat), action, Views.Payload(payload));

    /// <summary>Те саме, що Act: у реалтаймі Input іде тим самим Game.Act.</summary>
    public void Input(int seat, string action, object? payload = null) => Act(seat, action, payload);

    /// <summary>n тиків батька (годинник +20 мс кожен).</summary>
    public void Tick(int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            Clock.AdvanceMs(ParentTickMs);
            Host.Tick();
        }
    }

    /// <summary>n кроків самої підгри (годинник + її TickMs кожен).</summary>
    public void TickSub(int n = 1) => Tick(n * Math.Max(1, Game.Info.TickMs) / ParentTickMs);

    /// <summary>Тикати до кінця, але не довше за стелю + 1 с; вертає результат (null — гра не скінчилась навіть стелею: баг).</summary>
    public MinigameResult? RunToEnd(Action<PartyHarness>? everyTick = null)
    {
        var limit = (Host.CapMs + 1000) / ParentTickMs;
        for (var i = 0; i < limit && !Host.Over; i++)
        {
            everyTick?.Invoke(this);
            Tick();
        }
        return Host.Result;
    }

    /// <summary>Коли стартували — для «кінець ≤ стелі»: <c>h.Clock.UtcNow - h.StartedAt</c>.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Вид місця підгри (null — глядач) як на дроті.</summary>
    public JsonElement View(int? seat) => Views.Json(Game.View(seat));

    public JsonElement? Frame() => Host.Frame() is { } f ? Views.Json(f) : null;

    /// <summary>Батьківський стіл: Finish не має доходити ніколи, Log/Say збираються, Score/Award рахуються.</summary>
    public sealed class ParentContext(FakeClock clock, int seed, int humans, IServiceProvider services) : IRoomContext
    {
        public string RoomId => "party-test";
        public int Players => humans;
        public int Round => 1;
        public Random Rng { get; } = new(seed);
        public IClock Clock => clock;
        public IReadOnlyDictionary<string, string> Options { get; } = new Dictionary<string, string>();
        public IServiceProvider Services => services;
        public HashSet<int> Away { get; } = [];
        public string? NickOf(int seat) => seat >= 0 && seat < humans && !Away.Contains(seat) ? $"гравець{seat}" : null;
        public bool Seated(int seat) => NickOf(seat) is not null;
        public int? HostSeat => humans > 0 ? 0 : null;
        public List<string> Logs { get; } = [];
        public List<string> Says { get; } = [];
        public int Finishes { get; private set; }
        public int Leaked { get; private set; }
        public void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null, string? verdict = null) => Finishes++;
        public void Log(string text) => Logs.Add(text);
        public void Say(string text) => Says.Add(text);
        public void Score(int seat, double value, int? attempts = null) => Leaked++;
        public void Award(int seat, int shards, string reason) => Leaked++;

        /// <summary>Місце батька для людського місця підгри.</summary>
        public int ParentOf(MinigameHost host, int sub) =>
            sub >= 0 && sub < host.Ctx.Seats.Count && !host.Ctx.Seats[sub].Bot ? host.Ctx.Seats[sub].Parent : -1;
    }
}
