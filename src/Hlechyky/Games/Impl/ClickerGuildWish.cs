namespace Hlechyky.Games.Impl;

/// <summary>Подяка дарувальникові бажання: друг його прийняв (ClickerGuildWish.cs). <see cref="By"/> — нік друга.</summary>
public sealed record WishCredit(string By, DateTimeOffset At);

/// <summary>
/// Бажання чарівного глека, подароване другові (v13 Г7). Їде тією ж поштою, що гостинці й похвала: у скриньку допомоги
/// отримувача лягає <see cref="GuildBoost"/> з видом <c>wish:&lt;ключ&gt;</c>, а дзвоник цеху будить його коло. Друг застосовує
/// бажання при отриманні (<c>Clicker.TakeBoosts</c>) і повідомляє сюди (<see cref="WishReceived"/>) — тоді дарувальникові
/// лягає подяка (+10 хв гри), яку він забере на своїй наступній дії (<see cref="TakeCredits"/>). Скринька забирається
/// один раз, тож і подяка — один раз.
/// </summary>
public sealed partial class ClickerGuildService
{
    /// <summary>Префікс виду допомоги в скриньці: <c>wish:feast</c>.</summary>
    public const string WishPrefix = "wish:";
    /// <summary>Скільки хвилин гри отримує дарувальник, коли друг прийняв його бажання.</summary>
    public const int WishThanksMinutes = 10;

    sealed partial class State
    {
        /// <summary>Подяки дарувальникам бажань: ключ дарувальника → хто прийняв. Старого стану нема — порожньо.</summary>
        public Dictionary<string, List<WishCredit>>? Credits { get; set; }
    }

    /// <summary>
    /// Послати бажання <paramref name="wish"/> другові. null — пішло; інакше — чому ні. Отримувач мусить бути людиною,
    /// що сідала за коло: дарувати в нікуди не даємо.
    /// </summary>
    public string? SendWish(string fromKey, string fromNick, string toNick, string wish, DateTimeOffset now)
    {
        var toKey = Key(toNick);
        var who = toNick.Trim();
        if (toKey.Length == 0) return "Кому дарувати? Обери гончаря";
        if (toKey == fromKey) return "Собі бажання береш просто так — дарувати його не треба 🙂";
        if (IsPerson is { } person && !person(who)) return $"{who} — не гончар, а гість: бажання йому не передати";
        if (!SatDownAtWheel(toKey)) return $"{who} ще не сідав за гончарне коло — бажанню нікуди стати";
        lock (_lock)
        {
            var s = S();
            if (!s.Boosts.TryGetValue(toKey, out var box)) s.Boosts[toKey] = box = [];
            box.Add(new GuildBoost(fromNick.Trim(), WishPrefix + wish, 0, now));
            if (box.Count > MailMax) box.RemoveRange(0, box.Count - MailMax);
            Save();
        }
        Ring(who, "wish", fromNick);
        return null;
    }

    /// <summary>Друг <paramref name="byNick"/> прийняв бажання від <paramref name="giverKey"/>: покласти подяку.</summary>
    public void WishReceived(string giverKey, string byNick, DateTimeOffset now)
    {
        if (giverKey.Length == 0) return;
        lock (_lock)
        {
            var s = S();
            s.Credits ??= new Dictionary<string, List<WishCredit>>(StringComparer.Ordinal);
            if (!s.Credits.TryGetValue(giverKey, out var list)) s.Credits[giverKey] = list = [];
            list.Add(new WishCredit(byNick.Trim(), now));
            if (list.Count > MailMax) list.RemoveRange(0, list.Count - MailMax);
            Save();
        }
    }

    /// <summary>Забрати подяки за свої подаровані бажання. Порожньо — null, і нічого не пишемо.</summary>
    public List<WishCredit>? TakeCredits(string nickKey)
    {
        lock (_lock)
        {
            var s = S();
            if (s.Credits is null || !s.Credits.Remove(nickKey, out var list) || list.Count == 0) return null;
            Save();
            return list;
        }
    }
}
