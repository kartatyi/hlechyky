namespace Hlechyky.Padel;

/// <summary>
/// Половина грошей: збори на гру, витрати й загальний баланс, банки, рейтинг Ело, статистика й відзнаки.
/// ЗАГЛУШКА каркаса — заповнює агент грошей (контракт D:/or-wt/_tools/padel-contract.md, розділи «Збори»,
/// «Гроші», «Банки», «Рейтинг і відзнаки»). Реєструє: IPadelAgenda і все своє.
/// </summary>
public static class PadelMoneySetup
{
    public static void Add(IServiceCollection services) { }

    public static void Map(RouteGroupBuilder api) { }

    /// <summary>Після побудови застосунку: підняти фонові таймери (нагадування про збір).</summary>
    public static void Start(IServiceProvider sp) { }
}
