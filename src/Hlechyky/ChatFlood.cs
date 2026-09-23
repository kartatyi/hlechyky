namespace Hlechyky;

/// <summary>
/// Захист балачок від флуду: не більше <see cref="Burst"/> повідомлень за <see cref="Window"/> від одного ніка (разом
/// по всіх балачках — інакше флудер просто пересів би за стіл) і жодного однакового повідомлення поспіль в одній
/// балачці протягом <see cref="RepeatWindow"/>. Один на весь сервер: для Балачок, балачок столів і агентів.
/// </summary>
public sealed class ChatFlood
{
    public const int Burst = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Коротке («так», «+», «ні», «ага») повторювати можна: на два різні питання поспіль відповідають тим самим
    /// словом, і це розмова, а не флуд. Від флуду таких однаково береже <see cref="Burst"/>.
    /// </summary>
    public const int RepeatMinChars = 4;
    /// <summary>Скільки ніків пам'ятати, перш ніж прибрати тих, хто давно мовчить.</summary>
    const int PruneAt = 500;

    public const string TooFast = "Не так швидко — дай іншим слово вставити";
    public const string Repeat = "Це вже написано";

    readonly object _lock = new();
    readonly Dictionary<string, Trail> _byNick = new(StringComparer.OrdinalIgnoreCase);

    sealed class Trail
    {
        public readonly Queue<DateTimeOffset> Times = new();
        /// <summary>Останнє сказане в кожній балачці: «chat» — загальні, «table:&lt;id&gt;» — стіл.</summary>
        public readonly Dictionary<string, (string Text, DateTimeOffset At)> Last = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// null — можна (і повідомлення вже враховано), інакше текст відмови тому, хто писав. <paramref name="channel"/> —
    /// у якій балачці: «так» за столом і «так» у загальних Балачках — не повтор. Команди (/кубик) на повтор не
    /// перевіряються: кинути кубик двічі поспіль — нормальна справа.
    /// </summary>
    public string? Check(string nick, string text, DateTimeOffset now, string channel = "chat")
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
            var watch = !norm.StartsWith('/') && norm.Length >= RepeatMinChars;
            if (watch && t.Last.TryGetValue(channel, out var last) && last.Text == norm && now - last.At < RepeatWindow) return Repeat;
            if (t.Times.Count >= Burst) return TooFast;
            t.Times.Enqueue(now);
            if (watch) t.Last[channel] = (norm, now);
            return null;
        }
    }

    /// <summary>Забути тих, хто вже давно нічого не писав: для них ні вікно, ні повтор уже нічого не важать.</summary>
    void Prune(DateTimeOffset now)
    {
        var quiet = TimeSpan.FromTicks(Math.Max(Window.Ticks, RepeatWindow.Ticks));
        foreach (var nick in _byNick.Where(p => (p.Value.Times.Count == 0 || now - p.Value.Times.Last() >= quiet)
                     && p.Value.Last.Values.All(l => now - l.At >= quiet)).Select(p => p.Key).ToList())
            _byNick.Remove(nick);
    }
}
