using System.Text.Json;
using Hlechyky.Padel;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Tests.Padel;

/// <summary>
/// Половина грошей «Падельні» на тимчасовій базі: збори, витрати, рейтинг. Гравці, історія, лобі й розсилка —
/// заглушки (заглушки вкладені в клас, щоб не перетнутись іменами з тестами половини гри).
/// Годинник стоїть на 2026-09-10 12:00 UTC (15:00 за Києвом).
/// </summary>
public sealed class PadelMoneyRig : IDisposable
{
    /// <summary>Гравці: акаунт — нік із pid, гість — ім'я зі словника; прив'язки гостей — у <see cref="Links"/>.</summary>
    public sealed class Players : IPadelPlayers
    {
        public Dictionary<string, string> Guests { get; } = new() { ["g:1"] = "Петро", ["g:2"] = "Іра", ["g:3"] = "Стас" };
        public Dictionary<string, string> Links { get; } = [];
        public string Name(string pid) => Pid.IsUser(pid) ? pid[2..] : Guests.GetValueOrDefault(pid, "?");
        public string Canon(string pid) => Links.GetValueOrDefault(pid, pid);
        public bool Exists(string pid) => Pid.IsUser(pid) || Guests.ContainsKey(pid);
        public PadelPlayer Player(string pid) => new(pid, Name(pid), Pid.IsGuest(pid), Links.GetValueOrDefault(pid));
    }

    public sealed class History : IPadelHistory
    {
        public List<PadelResult> Res { get; } = [];
        public List<PadelTourResult> Tours { get; } = [];
        public IReadOnlyList<PadelResult> Results() => [.. Res];
        public IReadOnlyList<PadelTourResult> Tournaments() => [.. Tours];
    }

    public sealed class Wire : IPadelWire
    {
        readonly object _lock = new();
        public List<JsonElement> Gatherings { get; } = [];
        public List<(string Nick, string Text)> Toasts { get; } = [];
        public int Money, Rating, Lobby;
        public void Match(object view) { }
        public void Tournament(object view) { }
        public void Gathering(object view) { lock (_lock) Gatherings.Add(Views.Json(view)); }
        void IPadelWire.Money() { lock (_lock) Money++; }
        void IPadelWire.Rating() { lock (_lock) Rating++; }
        void IPadelWire.Lobby(object summary) { lock (_lock) Lobby++; }
        public void Toast(string nick, string text) { lock (_lock) Toasts.Add((nick, text)); }
        public void Chat(object line) { }
    }

    public TempDb Tmp { get; } = new();
    public FakeClock Clock { get; } = new();
    public Players Who { get; } = new();
    public History Hist { get; } = new();
    public Wire Out { get; } = new();
    public PadelOptions Options { get; } = new();
    public PadelGatherStore GatherStore { get; }
    public PadelMoneyStore MoneyStore { get; }
    public PadelBadgeStore BadgeStore { get; }
    public PadelGather Gather { get; }
    public PadelMoney Money { get; }
    public PadelRating Rating { get; }

    public PadelMoneyRig()
    {
        GatherStore = new PadelGatherStore(Tmp.Db);
        MoneyStore = new PadelMoneyStore(Tmp.Db);
        BadgeStore = new PadelBadgeStore(Tmp.Db);
        Gather = NewGather(GatherStore);
        Money = new PadelMoney(MoneyStore, Gather, Who, Out, Clock, Opt, NullLogger<PadelMoney>.Instance);
        Rating = new PadelRating(Hist, Who, GatherStore, BadgeStore, Out, Clock, NullLogger<PadelRating>.Instance);
    }

    IOptions<PadelOptions> Opt => Microsoft.Extensions.Options.Options.Create(Options);

    /// <summary>Свіжий сервіс зборів на тій самій базі — як після рестарту сервера.</summary>
    public PadelGather NewGather(PadelGatherStore? store = null) =>
        new(store ?? new PadelGatherStore(Tmp.Db), MoneyStore ?? new PadelMoneyStore(Tmp.Db), Who, Out, Clock, Opt,
            () => new NoPadelLobby(), NullLogger<PadelGather>.Instance);

    public static PadelMoneyActor U(string nick, bool admin = false) => new("u:" + nick, admin);
    public static readonly PadelMoneyActor SiteGuest = new(null, false);

    /// <summary>Код і тіло відповіді так, як їх побачить браузер.</summary>
    public static (int Status, JsonElement Body) R(IResult r) { var (s, b) = Radio.Reply(r); return (s ?? 200, b); }

    /// <summary>Київський час «через стільки хвилин» у форматі форми збору.</summary>
    public string LocalIn(double minutes) =>
        TimeZoneInfo.ConvertTime(Clock.UtcNow.AddMinutes(minutes), Hlechyky.Games.Days.Kyiv).ToString("yyyy-MM-ddTHH:mm");

    /// <summary>Збір від влада: id ("g1").</summary>
    public string NewGathering(string by = "влад", double hours = 1.5, int courts = 1, int? slots = null, double inMinutes = 24 * 60, string place = "Padel Club Позняки")
    {
        var (s, b) = R(Gather.Create(U(by), new PadelGather.Form(LocalIn(inMinutes), hours, courts, place, null, slots)));
        Assert.Equal(200, s);
        return b.GetProperty("gathering").GetProperty("id").GetString()!;
    }

    public JsonElement Join(string gid, string by, string? pid = null, bool? racket = null)
    {
        var (s, b) = R(Gather.Join(U(by), gid, pid, racket));
        Assert.True(s == 200, b.ToString());
        return b.GetProperty("gathering");
    }

    static int _n;
    /// <summary>Результат матчу: команди, переможець, рахунок сетів або очок.</summary>
    public PadelResult Result(string[] a, string[] b, int winner, int[][]? sets = null, int[]? points = null, PadelFacts? facts = null,
        DateTimeOffset? at = null, string source = "live")
    {
        var r = new PadelResult("r" + Interlocked.Increment(ref _n), at ?? Clock.UtcNow, source, null, points is null ? "match" : "points",
            [a, b], sets ?? (points is null ? [[6, 4]] : []), points, winner, facts ?? PadelFacts.None);
        Hist.Res.Add(r);
        Clock.Advance(TimeSpan.FromMinutes(1));
        return r;
    }

    public void Dispose() => Tmp.Dispose();
}
