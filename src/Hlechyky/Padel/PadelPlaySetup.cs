namespace Hlechyky.Padel;

/// <summary>
/// Половина гри: гравці й гості, табло (живі матчі, голос, годинник оренди), турніри, лобі. ЗАГЛУШКА каркаса —
/// заповнює агент гри (контракт D:/or-wt/_tools/padel-contract.md, розділи «Гравці», «Табло», «Турніри», «Лобі»).
/// Реєструє: IPadelPlayers, IPadelHistory і все своє.
/// </summary>
public static class PadelPlaySetup
{
    public static void Add(IServiceCollection services) { }

    public static void Map(RouteGroupBuilder api) { }

    /// <summary>Після побудови застосунку: підняти фонові таймери (годинник оренди, прибирання закинутих матчів).</summary>
    public static void Start(IServiceProvider sp) { }
}
