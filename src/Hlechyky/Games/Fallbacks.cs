using System.Collections.Concurrent;

namespace Hlechyky.Games;

/// <summary>
/// Заглушка ставок на випадок, коли економіки (WP1) в контейнері ще нема: баланс нульовий, тож жодна
/// ставка більша за нуль не проходить, а виплачувати нема з чого. Реєструється через TryAdd, і справжня
/// економіка перекриває її через <c>services.Replace(...)</c> у EconomySetup.
/// </summary>
public sealed class NoStakes : IStakes
{
    public int Balance(string nick) => 0;
    public bool TrySpend(string nick, int amount, string reason, string refKey) => amount <= 0;
    public void Grant(string nick, int amount, string reason, string refKey) { }
}

/// <summary>
/// Поштова скринька, яка знаходить <see cref="Broadcaster"/> лише тоді, коли в неї вперше щось кладуть.
///
/// Без цієї відстрочки граф залежностей замикається в коло: <c>Rooms</c> просить <c>IStakes</c>
/// (економіка), економіка — <c>IOutbox</c>, а <c>Broadcaster</c> знову просить <c>Rooms</c>. Поки
/// каркас і сервіси жили в різних гілках, кола не було видно; після злиття контейнер на такому колі
/// завмирає ще до того, як Kestrel відкриє порт — сервер просто мовчки не піднімається.
/// </summary>
/// <param name="find">Де взяти справжню скриньку. Кличеться не раніше за перше повідомлення.</param>
public sealed class DeferredOutbox(Func<IOutbox> find) : IOutbox
{
    // без замка: у найгіршому разі два потоки знайдуть той самий синглтон двічі, і це нікому не шкодить
    IOutbox? _real;

    public void Post(Outgoing message) => (_real ??= find()).Post(message);
}

/// <summary>Сховище станів у пам'яті: працює до рестарту. Справжнє (поверх Db) дає WP1.</summary>
public sealed class MemoryGameStore : IGameStore
{
    readonly ConcurrentDictionary<string, string> _states = new(StringComparer.Ordinal);

    public void SaveState(string key, string json) => _states[key] = json;
    public string? LoadState(string key) => _states.TryGetValue(key, out var json) ? json : null;
    public void DeleteState(string key) => _states.TryRemove(key, out _);
}
