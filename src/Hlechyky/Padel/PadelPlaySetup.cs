using Hlechyky.Games;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hlechyky.Padel;

public sealed record PadelGuestRequest(string? Name);
public sealed record PadelLinkRequest(string? Guest, string? Nick);

/// <summary>
/// Половина гри: гравці й гості, табло (живі матчі, голос, годинник оренди), турніри, лобі (контракт
/// D:/or-wt/_tools/padel-contract.md, §2). Реєструє IPadelPlayers, IPadelHistory, IPadelLobby і все своє —
/// через AddSingleton, щоб заглушки каркаса (TryAdd після нас) не перебили.
/// </summary>
public static class PadelPlaySetup
{
    /// <summary>Таймер тримаємо тут — інакше збирач сміття прибере його разом із годинником оренди.</summary>
    static Timer? _clock;

    public static void Add(IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<PadelPlayers>();
        services.AddSingleton<IPadelPlayers>(sp => sp.GetRequiredService<PadelPlayers>());
        services.AddSingleton<IPadelVoice, TtsPadelVoice>();
        services.AddSingleton(sp => new PadelPing(sp.GetRequiredService<IPadelWire>(), sp.GetRequiredService<IClock>())
        {
            Summary = () => sp.GetRequiredService<IPadelLobby>().Summary(),
        });
        services.AddSingleton<PadelMatches>();
        services.AddSingleton<PadelTours>();
        services.AddSingleton<IPadelHistory, PadelHistory>();
        services.AddSingleton<IPadelLobby, PadelLobby>();
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/players", Players);
        api.MapPost("/guests", AddGuest);
        api.MapPost("/guests/link", Link);
        api.MapPost("/guests/unlink", Unlink);

        api.MapPost("/matches", (HttpContext c, PadelMatchRequest b, PadelMatches m) => m.Create(PadelWho.Of(c), b).Http());
        api.MapGet("/matches", Matches);
        api.MapGet("/matches/{id}", (string id, PadelMatches m) => m.View(id) is { } v ? Results.Json(v) : PadelSetup.Fail("Нема такого матчу", 404));
        api.MapPost("/matches/{id}/act", (string id, HttpContext c, PadelActRequest b, PadelMatches m) => m.Act(id, PadelWho.Of(c), b).Http());
        api.MapGet("/matches/{id}/stats", (string id, PadelMatches m) => m.Stats(id) is { } v ? Results.Json(v) : PadelSetup.Fail("Нема такого матчу", 404));
        api.MapGet("/voice/{file}", Voice);

        api.MapGet("/tournaments/plan", (string? format, int? n, int? courts, string? total, int? minutes, double? booking, int? women) =>
            PadelTours.PlanFor(format, n ?? 0, courts ?? 1, total, minutes, booking, women).Http());
        api.MapPost("/tournaments", (HttpContext c, PadelTourRequest b, PadelTours t) => t.Create(PadelWho.Of(c), b).Http());
        api.MapGet("/tournaments", (PadelTours t) => Results.Json(t.List()));
        api.MapGet("/tournaments/{id}", (string id, PadelTours t) => t.View(id) is { } v ? Results.Json(v) : PadelSetup.Fail("Нема такого турніру", 404));
        api.MapPost("/tournaments/{id}/score", (string id, HttpContext c, PadelScoreRequest b, PadelTours t) => t.Score(id, PadelWho.Of(c), b).Http());
        api.MapPost("/tournaments/{id}/live", (string id, HttpContext c, PadelCourtRequest b, PadelTours t) => t.Live(id, PadelWho.Of(c), b).Http());
        api.MapPost("/tournaments/{id}/rounds", (string id, HttpContext c, PadelRoundsRequest b, PadelTours t) => t.AddRounds(id, PadelWho.Of(c), b).Http());
        api.MapPost("/tournaments/{id}/finish", (string id, HttpContext c, PadelTours t) => t.Finish(id, PadelWho.Of(c)).Http());
        api.MapPost("/tournaments/{id}/delete", (string id, HttpContext c, PadelTours t) => t.Delete(id, PadelWho.Of(c)).Http());

        api.MapGet("/lobby", (IPadelLobby l) => Results.Json(l.Summary()));
    }

    /// <summary>Після побудови застосунку: зв'язати турніри з матчами, наперед озвучити службове, підняти годинник оренди.</summary>
    public static void Start(IServiceProvider sp)
    {
        sp.GetRequiredService<PadelTours>();
        var matches = sp.GetRequiredService<PadelMatches>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Padel");
        try { matches.Warm(); } catch (Exception ex) { log.LogWarning(ex, "Падельня не поставила службові фрази в озвучку"); }
        _clock = new Timer(_ =>
        {
            try { matches.Tick(); }
            catch (Exception ex) { log.LogWarning(ex, "Годинник оренди Падельні спіткнувся"); }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    // ------------------------------------------------------------------ гравці

    /// <summary>Усі, хто бодай раз був у матчі чи турнірі (і всі гості) — спершу ті, хто грав нещодавно; і всі акаунти.</summary>
    public static IResult Players(PadelPlayers players, PadelMatches matches, PadelTours tours)
    {
        var seen = new Dictionary<string, (int Played, DateTimeOffset? Last)>(StringComparer.Ordinal);
        void Hit(string pid, DateTimeOffset at, int played)
        {
            var (p, l) = seen.TryGetValue(pid, out var x) ? x : (0, null);
            seen[pid] = (p + played, l is { } ll && ll > at ? ll : at);
        }
        foreach (var m in matches.All().Where(m => m.Status != "abandoned"))
            foreach (var p in m.Teams.SelectMany(t => t)) Hit(p, m.LastAt, m.Status == "done" ? 1 : 0);
        foreach (var (pid, at) in tours.Rosters()) Hit(pid, at, 0);
        foreach (var (_, _, m) in tours.Scored())
            foreach (var p in m.A.Concat(m.B)) Hit(p, m.At ?? DateTimeOffset.MinValue, 1);
        foreach (var g in players.Guests()) if (!seen.ContainsKey(g.Pid)) seen[g.Pid] = (0, null);
        var list = seen.Select(kv =>
            {
                var p = players.Player(kv.Key);
                return new { pid = p.Pid, name = p.Name, guest = p.Guest, linkedTo = p.LinkedTo, played = kv.Value.Played, last = kv.Value.Last };
            })
            .OrderByDescending(x => x.last ?? DateTimeOffset.MinValue).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToArray();
        return Results.Json(new
        {
            players = list,
            accounts = players.Accounts().Select(a => new { pid = a.Pid, name = a.Name }).ToArray(),
        });
    }

    public static IResult AddGuest(HttpContext c, PadelGuestRequest b, PadelPlayers players)
    {
        if (!Auth.IsUser(c) && !Auth.IsAdmin(c)) return PadelSetup.Fail("Вписувати гостей можуть лише акаунти — увійди на головній", 403);
        return players.AddGuest(b.Name, Auth.Nick(c)).Http();
    }

    public static IResult Link(HttpContext c, PadelLinkRequest b, PadelPlayers players, IPadelWire wire, PadelMoney money, ILoggerFactory logs) =>
        LinkGuest(PadelWho.Of(c), b, players, money.Touches, wire, logs.CreateLogger("Padel")).Http();

    /// <summary>
    /// Прив'язати гостя: собі — будь-який акаунт, до когось іншого — адмін. Гість із грошима в розрахунках переносить
    /// і борги, тож його прив'язує лише адмін; решту — не тихо: тому, хто гостя вписав, — тост, у лог — рядок.
    /// </summary>
    public static PadelReply LinkGuest(PadelWho who, PadelLinkRequest b, PadelPlayers players, Func<string, bool> hasMoney,
        IPadelWire wire, ILogger log)
    {
        var me = who.Pid;
        var admin = who.Admin;
        if (me is null && !admin) return PadelReply.No("Прив'язувати можуть лише акаунти — увійди на головній", 403);
        var target = string.IsNullOrWhiteSpace(b.Nick) ? me : Pid.User(b.Nick);
        if (target is null) return PadelReply.No("Кого прив'язати — нік?");
        if (!admin && target != me) return PadelReply.No("Прив'язати гостя можна лише до себе", 403);
        var guest = b.Guest?.Trim();
        var was = guest is not null && Pid.IsGuest(guest) ? players.Player(guest).LinkedTo : null;
        if (was is not null && was != target && !admin) return PadelReply.No($"Цього гостя вже прив'язано до {players.Name(was)}");
        if (!admin && was is null && guest is not null && Pid.IsGuest(guest) && hasMoney(guest))
            return PadelReply.No("У цього гостя є гроші в розрахунках — прив'язати може адмін");
        var r = players.Link(guest, target);
        if (r.Error is not null || was == target) return r;
        wire.Rating();
        var name = players.Name(guest!);
        log.LogInformation("Падельня: гостя {Guest} ({Name}) прив'язано до {Target}, прив'язав {Who}", guest, name, target, who.Name);
        if (players.CreatedBy(guest!) is { } author && author != me && author != target)
            wire.Toast(Pid.NickKey(author)!, $"🔗 {who.Name}: гостя «{name}» прив'язано до акаунта {players.Name(target)}");
        return r;
    }

    public static IResult Unlink(HttpContext c, PadelLinkRequest b, PadelPlayers players, IPadelWire wire)
    {
        if (!Auth.IsAdmin(c)) return PadelSetup.Fail("Відв'язати може лише адмін", 403);
        var r = players.Link(b.Guest, null);
        if (r.Error is null) wire.Rating();
        return r.Http();
    }

    // ------------------------------------------------------------------ табло

    public static IResult Matches(int? live, int? recent, PadelMatches m) =>
        Results.Json(new { matches = recent is { } n && live is null ? m.Recent(n) : m.Live() });

    /// <summary>Кліп голосу табло — лише для фраз, які сервер сам склав (інакше 404).</summary>
    public static IResult Voice(string file, IPadelVoice voice)
    {
        var id = file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        if (id.Length != 16 || !id.All(Uri.IsHexDigit)) return Results.NotFound();
        return voice.File(id) is { } path && File.Exists(path) ? Results.File(path, "audio/mpeg") : Results.NotFound();
    }
}
