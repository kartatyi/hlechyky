using Hlechyky.Games;
using Microsoft.Extensions.Options;

namespace Hlechyky.Padel;

/// <summary>
/// Половина грошей: збори на гру, витрати й загальний баланс, банки, рейтинг Ело, статистика й відзнаки
/// (контракт D:/or-wt/_tools/padel-contract.md, §3). Реєструє IPadelAgenda і все своє.
/// </summary>
public static class PadelMoneySetup
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<PadelGatherStore>();
        services.AddSingleton<PadelMoneyStore>();
        services.AddSingleton<PadelBadgeStore>();
        // Лобі — лінивим посиланням: клас лобі половини гри сам читає агенду (збори), пряма залежність замкнула б коло
        services.AddSingleton(sp => new PadelGather(sp.GetRequiredService<PadelGatherStore>(), sp.GetRequiredService<PadelMoneyStore>(),
            sp.GetRequiredService<IPadelPlayers>(), sp.GetRequiredService<IPadelWire>(), sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IOptions<PadelOptions>>(), () => sp.GetRequiredService<IPadelLobby>(),
            sp.GetRequiredService<ILogger<PadelGather>>()));
        services.AddSingleton<IPadelAgenda>(sp => sp.GetRequiredService<PadelGather>());
        services.AddSingleton<PadelMoney>();
        services.AddSingleton<PadelRating>();
        services.AddSingleton<PadelMoneyTicker>();
    }

    public static void Map(RouteGroupBuilder api)
    {
        PadelGatherApi.Map(api);
        PadelMoneyApi.Map(api);
        PadelRatingApi.Map(api);
    }

    /// <summary>Після побудови застосунку: нагадування про збір (раз на хвилину) і відзнаки (раз на 10 с).</summary>
    public static void Start(IServiceProvider sp) => sp.GetRequiredService<PadelMoneyTicker>().Begin();
}

/// <summary>
/// Фонові таймери половини грошей. Перший прогін відзнак — за пару секунд після старту й мовчки (усе давнє лягає в
/// базу без тостів); нагадування — раз на хвилину, лише про збори, що ще не почались.
/// </summary>
public sealed class PadelMoneyTicker(PadelGather gather, PadelRating rating, IHostApplicationLifetime life,
    ILogger<PadelMoneyTicker> log) : IDisposable
{
    Timer? _remind, _badges;
    int _busyRemind, _busyBadges;

    public void Begin()
    {
        _badges = new Timer(_ => Run(ref _busyBadges, rating.CheckBadges, "відзнаки"), null, TimeSpan.FromSeconds(2), PadelRating.Fresh);
        _remind = new Timer(_ => Run(ref _busyRemind, gather.Remind, "нагадування"), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        life.ApplicationStopping.Register(Dispose);
    }

    void Run(ref int busy, Func<int> work, string what)
    {
        // Повільна база не мусить наганяти один прогін на інший
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try { work(); }
        catch (Exception ex) { log.LogWarning(ex, "Падельня: {What} спіткнулись", what); }
        finally { Volatile.Write(ref busy, 0); }
    }

    public void Dispose()
    {
        _remind?.Dispose();
        _badges?.Dispose();
    }
}
