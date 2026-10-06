using Microsoft.Extensions.Logging;

namespace Hlechyky.Games;

/// <summary>Рахунок одного ніка за столом у цій партії: скільки вніс, скільки забрав і скільки ключів леджера вже витрачено.</summary>
public sealed class TableAccount
{
    public string Nick { get; set; } = "";
    public int In { get; set; }
    public int Out { get; set; }
    /// <summary>Лічильник ключів table-in: росте на КОЖНУ спробу, і вдалу, і ні, — ключ ніколи не повторюється.</summary>
    public int InN { get; set; }
    /// <summary>Лічильник ключів table-out (виплати й повернення).</summary>
    public int OutN { get; set; }

    public TableAccount Clone() => new() { Nick = Nick, In = In, Out = Out, InN = InN, OutN = OutN };
}

/// <summary>
/// Банк столу (гра на N на черепки, покер): облік на кімнаті, скільки кожен нік вніс і скільки отримав у цій партії.
/// Каркас не дає виплатити більше, ніж лежить (<see cref="Held"/>), і входить у знімок перезапуску. Новий раунд
/// («Ще раз») — новий банк: ключі леджера містять раунд.
/// </summary>
public sealed class TableBank
{
    /// <summary>Ключ — <c>Rooms.NickKey</c>.</summary>
    public Dictionary<string, TableAccount> Accounts { get; set; } = new(StringComparer.Ordinal);

    public long TotalIn => Accounts.Values.Sum(a => (long)a.In);
    public long TotalOut => Accounts.Values.Sum(a => (long)a.Out);
    /// <summary>Скільки черепків зараз лежить за столом.</summary>
    public long Held => TotalIn - TotalOut;
    /// <summary>За столом ходили черепки.</summary>
    public bool Used => Accounts.Values.Any(a => a.In > 0);

    public TableAccount Of(string nick)
    {
        var key = Rooms.NickKey(nick);
        if (!Accounts.TryGetValue(key, out var a)) Accounts[key] = a = new TableAccount { Nick = nick.Trim() };
        return a;
    }

    public TableBank Clone()
    {
        var b = new TableBank();
        foreach (var (k, a) in Accounts) b.Accounts[k] = a.Clone();
        return b;
    }

    /// <summary>
    /// Кому скільки повернути, щоб банк спорожнів: що сказала гра (<see cref="Game.SettleTable"/>), якщо це сходиться рівно з
    /// <see cref="Held"/> і лише для тих, хто за столом щось вносив; інакше — «вніс мінус забрав», додатне, до вичерпання банку.
    /// </summary>
    public List<(string Nick, int Amount)> Owed(IReadOnlyDictionary<string, int>? fromGame, ILogger log, string roomId)
    {
        var held = Held;
        var list = new List<(string Nick, int Amount)>();
        if (held <= 0) return list;
        if (fromGame is not null
            && fromGame.Values.All(v => v >= 0)
            && fromGame.Values.Sum(v => (long)v) == held
            && fromGame.Keys.All(n => Accounts.ContainsKey(Rooms.NickKey(n))))
        {
            foreach (var (nick, amount) in fromGame)
                if (amount > 0) list.Add((Accounts[Rooms.NickKey(nick)].Nick, amount));
            return list;
        }
        if (fromGame is not null)
            log.LogWarning("стіл {Room}: розрахунок гри не сходиться з банком ({Held}) — повертаю «вніс мінус забрав»", roomId, held);
        var left = held;
        foreach (var a in Accounts.Values)
        {
            if (left <= 0) break;
            var due = (int)Math.Min(left, Math.Max(0, a.In - a.Out));
            if (due <= 0) continue;
            list.Add((a.Nick, due));
            left -= due;
        }
        return list;
    }
}
