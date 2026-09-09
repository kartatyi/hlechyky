using System.Collections.Concurrent;

namespace Hlechyky.Games;

/// <summary>
/// Квота на секунду з одного з'єднання (ARCHITECTURE §9): десять дій і тридцять реалтайм-вводів. Живе окремо
/// від хаба саме для того, щоб її можна було перевірити тестом — SignalR у тестах не піднімається (TESTING.md §1).
/// </summary>
public sealed class RateGate
{
    /// <summary>Скільки дій (створити, сісти, походити) можна на секунду з одного з'єднання.</summary>
    public const int ActsPerSecond = 10;
    /// <summary>Скільки реалтайм-вводів можна на секунду: 25 Гц гри плюс запас на подвійні натиски.</summary>
    public const int InputsPerSecond = 30;

    readonly ConcurrentDictionary<string, (long Second, int Acts, int Inputs)> _rates = new(StringComparer.Ordinal);

    /// <summary>
    /// Чи пропускаємо цей виклик. Рішення береться рівно з тієї спроби, яка справді записалась: фабрику
    /// <c>AddOrUpdate</c> контейнер має право покликати кілька разів, тому рахуємо явним циклом.
    /// </summary>
    public bool Allow(string connId, bool input, long second)
    {
        var limit = input ? InputsPerSecond : ActsPerSecond;
        while (true)
        {
            if (!_rates.TryGetValue(connId, out var old))
            {
                if (_rates.TryAdd(connId, (second, input ? 0 : 1, input ? 1 : 0))) return true;
                continue;
            }
            if (old.Second != second)
            {
                if (_rates.TryUpdate(connId, (second, input ? 0 : 1, input ? 1 : 0), old)) return true;
                continue;
            }
            var used = input ? old.Inputs : old.Acts;
            if (used >= limit) return false;
            var next = (second, old.Acts + (input ? 0 : 1), old.Inputs + (input ? 1 : 0));
            if (_rates.TryUpdate(connId, next, old)) return true;
        }
    }

    /// <summary>З'єднання закрилось — лічильник більше ні до чого.</summary>
    public void Forget(string connId) => _rates.TryRemove(connId, out _);

    /// <summary>Скільки з'єднань зараз пам'ятаємо (для тестів і діагностики).</summary>
    public int Tracked => _rates.Count;
}
