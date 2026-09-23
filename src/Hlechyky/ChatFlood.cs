namespace Hlechyky;

/// <summary>
/// Захист балачок від флуду: не більше <see cref="Burst"/> повідомлень за <see cref="Window"/> від одного ніка і
/// жодного однакового повідомлення поспіль протягом <see cref="RepeatWindow"/>. Один на весь сервер — і для Балачок,
/// і для балачок столів, і для агентів: інакше флудер просто пересів би за стіл.
/// </summary>
public sealed class ChatFlood
{
    public const int Burst = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);
    /// <summary>Скільки ніків пам'ятати, перш ніж прибрати тих, хто давно мовчить.</summary>
    const int PruneAt = 500;

    public const string TooFast = "Не так швидко — дай іншим слово вставити";
    public const string Repeat = "Це вже написано";

    readonly object _lock = new();
    readonly Dictionary<string, Trail> _byNick = new(StringComparer.OrdinalIgnoreCase);

    sealed class Trail
    {
        public readonly Queue<DateTimeOffset> Times = new();
        public string Last = "";
        public DateTimeOffset LastAt;
    }

    /// <summary>
    /// null — можна (і повідомлення вже враховано), інакше текст відмови тому, хто писав. Команди (/кубик) не
    /// перевіряються на повтор: кинути кубик двічі поспіль — нормальна справа.
    /// </summary>
    public string? Check(string nick, string text, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_byNick.TryGetValue(nick, out var t))
            {
                if (_byNick.Count >= PruneAt) Prune(now);
                _byNick[nick] = t = new Trail();
            }
            while (t.Times.Count > 0 && now - t.Times.Peek() >= Window) t.Times.Dequeue();
            var norm = text.Trim().ToLowerInvariant();
            var command = norm.StartsWith('/');
            if (!command && norm.Length > 0 && norm == t.Last && now - t.LastAt < RepeatWindow) return Repeat;
            if (t.Times.Count >= Burst) return TooFast;
            t.Times.Enqueue(now);
            if (!command) (t.Last, t.LastAt) = (norm, now);
            return null;
        }
    }

    /// <summary>Забути тих, хто вже давно нічого не писав: для них ні вікно, ні повтор уже нічого не важать.</summary>
    void Prune(DateTimeOffset now)
    {
        var quiet = TimeSpan.FromTicks(Math.Max(Window.Ticks, RepeatWindow.Ticks));
        foreach (var nick in _byNick.Where(p => now - p.Value.LastAt >= quiet
                     && (p.Value.Times.Count == 0 || now - p.Value.Times.Last() >= quiet)).Select(p => p.Key).ToList())
            _byNick.Remove(nick);
    }
}
