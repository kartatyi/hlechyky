using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Economy;

/// <summary>
/// Паспорти всіх ігор збірки — щоб людські тексти («перемога в шахах») і таблиці знали відмінок, групу
/// і порядок соло-результату, не залежачи від реєстру каркаса (WP0). Скан робиться раз, а події з
/// <see cref="RoomFinishedEvent"/> дозаповнюють мапу для ігор, які з'являться пізніше.
/// </summary>
public sealed class GameNames
{
    readonly ConcurrentDictionary<string, GameInfo> _map = new(StringComparer.Ordinal);

    public GameNames()
    {
        foreach (var t in typeof(Game).Assembly.GetTypes())
        {
            if (t.IsAbstract || !typeof(Game).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) is null) continue;
            try
            {
                if (Activator.CreateInstance(t) is Game g) _map[g.Info.Id] = g.Info;
            }
            catch (Exception) { /* гра, яку не створити без каркаса, нам тут не потрібна */ }
        }
    }

    public void Learn(GameInfo info) => _map[info.Id] = info;
    public GameInfo? Get(string id) => _map.TryGetValue(id, out var i) ? i : null;
    /// <summary>«шахи» — для «перемога в шахах». Нема паспорта — лишається Id, і це видно, а не падає.</summary>
    public string Accusative(string id) => Get(id)?.Accusative ?? id;
    public string Title(string id) => Get(id)?.Title ?? id;
    public IReadOnlyList<GameInfo> All => _map.Values.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    /// <summary>Id ігор, що входять у «Щоденний глек».</summary>
    public IReadOnlyList<string> Daily => typeof(Game).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(Game).IsAssignableFrom(t) && typeof(IDailyGame).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) is not null)
        .Select(t => { try { return (Activator.CreateInstance(t) as Game)?.Info.Id; } catch (Exception) { return null; } })
        .Where(id => id is not null).Select(id => id!).Distinct(StringComparer.Ordinal)
        .OrderBy(id => id, StringComparer.Ordinal).ToList();
}

/// <summary>Куди складати повідомлення, поки каркас розсилки ще не піднявся (і в тестах, де він не потрібен).</summary>
public sealed class NullOutbox : IOutbox
{
    public void Post(Outgoing message) { }
}

/// <summary>
/// Черепки: гаманці, леджер, стелі, ідемпотентність. Леджер — істина, wallets — кеш, який
/// <see cref="Rebuild"/> завжди може перерахувати. Кожен успішний рух грошей іде в
/// <see cref="IOutbox"/> як <see cref="WalletChanged"/> з людським текстом («+5 черепків: перемога в шахах»).
/// </summary>
public sealed class Economy(EconomyStore store, GameNames names, IClock clock,
    IOptionsMonitor<EconomyOptions> opts, IOutbox outbox) : IStakes
{
    /// <summary>Баланс змінився: нік і новий баланс. Слухають ачівки («Сотня»).</summary>
    public event Action<string, int>? Changed;

    EconomyOptions O => opts.CurrentValue;
    string Today => Days.Today(clock);

    public static string Key(string? nick) => EconomyStore.Key(nick);

    public int Balance(string nick) => store.Balance(Key(nick));

    public WalletRow Wallet(string nick)
    {
        var key = Key(nick);
        return store.Wallet(key) ?? new WalletRow(key, nick, 0, 0, 0);
    }

    /// <summary>Топ гаманців: by = "balance" (типово) або "earned".</summary>
    public List<(string Nick, int Balance, int Earned)> Top(int n, string by = "balance") =>
        store.TopShards(n, DateTimeOffset.MinValue, byBalance: by != "earned")
            .Select(r => (r.Nick, r.Balance, r.Earned)).ToList();

    /// <summary>Перерахувати гаманці з леджера (після ручного втручання в базу або збою).</summary>
    public void Rebuild() => store.Rebuild(clock.UtcNow);

    // ---------- нарахування ----------

    /// <summary>Нарахувати без стелі. <paramref name="refKey"/> — ключ ідемпотентності (повтор → Duplicate).</summary>
    public GrantResult Grant(string nick, int amount, string reason, string? refKey = null, string? text = null) =>
        Apply(nick, amount, reason, refKey, null, null, 0, 0, text);

    /// <summary>Нарахувати зі стелею дня: <paramref name="capUnits"/> — скільки «з'їдає» ця операція.</summary>
    public GrantResult GrantCapped(string nick, int amount, string reason, string? refKey,
        string capKey, int capLimit, int capUnits = 1, string? text = null) =>
        Apply(nick, amount, reason, refKey, null, capKey, capLimit, capUnits, text);

    /// <summary>
    /// Нарахувати зі стелею, де сам лічильник дає номер для ref: «десята хвилина слухання»,
    /// «третій обмін глеків». Так повтор не потребує окремої нумерації в тому, хто кличе.
    /// </summary>
    public GrantResult GrantSequenced(string nick, int amount, string reason, Func<int, string> refFor,
        string capKey, int capLimit, int capUnits = 1, string? text = null) =>
        Apply(nick, amount, reason, null, refFor, capKey, capLimit, capUnits, text);

    GrantResult Apply(string nick, int amount, string reason, string? refKey, Func<int, string>? refFor,
        string? capKey, int capLimit, int capUnits, string? text)
    {
        if (amount <= 0) return GrantResult.Applied;
        var key = Key(nick);
        if (key.Length == 0) return GrantResult.Applied;
        var (result, balance) = store.Grant(key, nick, amount, reason, refKey, refFor, capKey, Today, capLimit, capUnits, clock.UtcNow);
        if (result == GrantResult.Applied) Announce(nick, balance, amount, reason, text);
        return result;
    }

    // ---------- списання ----------

    /// <summary>
    /// Списати. Атомарно: або вистачило, або нічого не сталось. Повтор із тим самим ref — true без
    /// другого списання (щоб подвійний старт партії не з'їв дві ставки).
    /// </summary>
    public bool TrySpend(string nick, int amount, string reason, string? refKey = null)
    {
        if (amount <= 0) return true;
        var key = Key(nick);
        if (key.Length == 0) return false;
        var (ok, duplicate, balance) = store.Spend(key, nick, amount, reason, refKey, clock.UtcNow);
        if (ok && !duplicate) Announce(nick, balance, -amount, reason, null);
        return ok;
    }

    void Announce(string nick, int balance, int delta, string reason, string? text)
    {
        outbox.Post(new WalletChanged(nick, balance, delta, reason, text ?? Text(delta, reason)));
        Changed?.Invoke(nick, balance);
    }

    // ---------- IStakes (те, що просить каркас кімнат) ----------

    void IStakes.Grant(string nick, int amount, string reason, string refKey) => Grant(nick, amount, reason, refKey);

    // ---------- людські тексти ----------

    /// <summary>«+5 черепків: перемога в шахах», «−10 черепків: ставка».</summary>
    public string Text(int delta, string reason) =>
        $"{(delta < 0 ? "−" : "+")}{Math.Abs(delta)} {Shards(delta)}: {Reason(reason)}";

    /// <summary>Черепок / черепки / черепків — бо «+2 черепків» ріже око.</summary>
    public static string Shards(int n)
    {
        var abs = Math.Abs(n);
        if (abs % 100 is >= 11 and <= 14) return "черепків";
        return (abs % 10) switch { 1 => "черепок", 2 or 3 or 4 => "черепки", _ => "черепків" };
    }

    /// <summary>
    /// Код причини → те, що людина прочитає в тості. Назва гри йде через тире в називному відмінку:
    /// «перемога в шахах» вимагає місцевого відмінка, якого в <see cref="GameInfo"/> нема (там знахідний,
    /// «сіли грати в шахи»), а вигадувати відмінювання на льоту — гарантовано наступити на «дурня» і «Глек-слово».
    /// </summary>
    public string Reason(string reason)
    {
        var (head, tail) = Split(reason);
        return head switch
        {
            "listen" => "за те, що слухаєш",
            "win" => $"перемога — {names.Title(tail)}",
            "draw" => $"нічия — {names.Title(tail)}",
            "play" => $"за партію — {names.Title(tail)}",
            "solo" => $"результат — {names.Title(tail)}",
            "daily" => $"щоденний глек — {names.Title(tail)}",
            "ach" => "ачівка",
            "stake" => "ставка",
            "stake-win" => "банк за ставку",
            "stake-refund" => "ставку повернуто",
            "clicker" => "обмін глеків на черепки",
            "ad" => tail switch
            {
                "winner" => "перемога в конкурсі реклами",
                "vote" => "за голос у конкурсі реклами",
                _ => "за конкурс реклами",
            },
            "award" => "нагорода за гру",
            _ => reason,
        };
    }

    static (string Head, string Tail) Split(string reason)
    {
        var i = reason.IndexOf(':');
        return i < 0 ? (reason, "") : (reason[..i], reason[(i + 1)..]);
    }
}
