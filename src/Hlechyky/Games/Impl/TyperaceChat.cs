using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;

namespace Hlechyky.Games.Impl;

/// <summary>Репліка з Балачок, придатна для заїзду: хто, коли («26.09») і що — уже зведене до друкованого вигляду.</summary>
public sealed record TyperaceChatLine(long Id, string Nick, string Date, string Text);

/// <summary>
/// «Наші балачки» для Клавоперегонів: справжні репліки друзів із загальних Балачок як текст заїзду з підписом
/// «— Smaug, 26.09». Бережемо приватність і нерви: лише загальні Балачки (<c>kind = 'chat'</c>, не балачки столу, не
/// кубики й не записки), лише ті, хто має акаунт на сайті (так відсіюються агенти й персонажі Мафії), без посилань,
/// пошт, телефонів, адрес, латиниці й лайки, від 25 до 220 знаків. Банк збираємо фоном раз на годину; кімната бере
/// лише готовий список з пам'яті (під замком — жодного SQLite).
/// </summary>
public sealed partial class TyperaceChat(Db? db, IClock clock)
{
    public const string Chat = "chat", Mix = "mix";
    /// <summary>Менше реплік — «наші балачки» їдуть класикою з поясненням: три заїзди поспіль з одних і тих самих набриднуть.</summary>
    public const int MinLines = 12;
    public const int MinLen = 25, MaxLen = 220, MinWords = 4;
    /// <summary>Скільки останніх реплік проглядаємо: Балачки ростуть, а банк мусить лишатись легким.</summary>
    const int Scan = 5000;
    static readonly TimeSpan Every = TimeSpan.FromHours(1);

    volatile IReadOnlyList<TyperaceChatLine> _lines = [];
    DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    int _loading;
    volatile bool _loaded;

    /// <summary>Чи банк уже хоч раз дочитано (до того «наші балачки» пояснюють, що ще читаємо).</summary>
    public bool Loaded => _loaded || db is null;

    /// <summary>Готові репліки з пам'яті; застарілі — перечитуються фоном, а зараз віддаємо те, що є.</summary>
    public IReadOnlyList<TyperaceChatLine> Lines
    {
        get { Warm(); return _lines; }
    }

    /// <summary>Лише для тестів: підкласти репліки замість бази.</summary>
    public void Set(IEnumerable<TyperaceChatLine> lines)
    {
        _lines = [.. lines];
        _loaded = true;
    }

    public void Warm()
    {
        if (db is null || clock.UtcNow - _loadedAt < Every) return;
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        _ = Task.Run(Load);
    }

    /// <summary>Лише для тестів і прогріву: дочитати зараз.</summary>
    public void LoadNow() { _loading = 1; Load(); }

    void Load()
    {
        try
        {
            var raw = new List<(long Id, string Nick, string Text, string At)>();
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            db!.With(c =>
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT nick_key FROM accounts";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) accounts.Add(r.GetString(0));
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, nick, text, created_at FROM chat WHERE kind = 'chat' AND room_id IS NULL ORDER BY id DESC LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", Scan);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) raw.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
                }
            });
            var list = new List<TyperaceChatLine>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, nick, text, at) in raw)
            {
                if (!accounts.Contains(Auth.NickKey(nick))) continue;
                if (Clean(text) is not { } clean || !seen.Add(clean)) continue;
                list.Add(new TyperaceChatLine(id, nick.Trim(), DateOf(at), clean));
            }
            _lines = list;
            _loaded = true;
            _loadedAt = clock.UtcNow;
        }
        catch (Exception) { _loadedAt = clock.UtcNow - Every + TimeSpan.FromMinutes(5); /* спробуємо ще за п'ять хвилин */ }
        finally { _loading = 0; }
    }

    /// <summary>«2026-09-26T21:05:…Z» → «26.09» за київським часом.</summary>
    static string DateOf(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? TimeZoneInfo.ConvertTime(t, Days.Kyiv).ToString("dd.MM", CultureInfo.InvariantCulture)
            : "";

    [GeneratedRegex(@"https?:|www\.|\.(com|ua|net|org|ru|io)\b|вул\.|вулиц|просп|провул|квартир|кв\.\s*\d|будин\w*\s*№?\s*\d|під[’']їзд|пошт\w*\s|телефон|номер\w*\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex Private();

    // Лайка й образи — коренями. Рідні «блін» і «курча» не чіпаємо; «курва» — так.
    [GeneratedRegex(@"ху[йяєїю]|п[іи]зд|\b(за|від|на|у|в|по|роз|до|ви|при|з)?[єїе]б[аоуі]|йоб|бля|\bсук[аиуо]|курв|мудак|мудил|п[іи]д[ао]р|педик|гандон|залуп|дроч|шлюх|повія|мраз|виродок|дебіл|ідіот|придур|чмо\b|лох\b|гівн|срак|жид|хохл|москал|нігер", RegexOptions.IgnoreCase)]
    private static partial Regex Rude();

    /// <summary>
    /// Звести репліку до тексту заїзду або відкинути (null): без емодзі, зі звичними лапками й тире, лише друкована
    /// абетка, 25–220 знаків і щонайменше чотири слова, без приватного (посилання, адреси, телефони) і без лайки.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('/')) return null;
        var sb = new StringBuilder(raw.Length);
        var digits = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (char.IsSurrogate(ch)) continue;   // емодзі — геть, решта репліки лишається
            var cat = char.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.NonSpacingMark or UnicodeCategory.Format) continue;
            if (char.IsDigit(ch)) digits++;
            sb.Append(ch switch
            {
                '–' or '−' => '—',
                '“' or '”' or '„' => '"',
                _ => ch,
            });
        }
        // сім цифр і більше — схоже на телефон, картку чи адресу; не ризикуємо
        if (digits >= 7) return null;
        var s = TyperaceText.Normalize(sb.ToString()).Replace('\n', ' ').Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        if (s.Length is < MinLen or > MaxLen) return null;
        if (s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < MinWords) return null;
        if (Private().IsMatch(s) || Rude().IsMatch(s)) return null;
        return TyperaceText.IsTypeable(s) ? s : null;
    }

    /// <summary>
    /// Низка реплік під довжину: як прислів'я — перемішати, додавати, доки коротше за ціль. Підпис — «— нік, дата» кожної
    /// (понад три — «і ще N»). <paramref name="used"/> — пам'ять кімнати, як і для банку.
    /// </summary>
    public static TyperacePick Pick(IReadOnlyList<TyperaceChatLine> all, string length, Random rng, ISet<string> used)
    {
        var target = TyperaceBank.TargetLen(length);
        var pool = new List<TyperaceChatLine>(all.Count);
        foreach (var l in all) if (!used.Contains(IdOf(l))) pool.Add(l);
        if (pool.Count < 3)
        {
            foreach (var l in all) used.Remove(IdOf(l));
            pool.Clear();
            pool.AddRange(all);
        }
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var parts = new List<TyperaceChatLine>();
        var len = 0;
        foreach (var l in pool)
        {
            if (len >= target - 20) break;
            var add = l.Text.Length + (parts.Count > 0 ? 1 : 0);
            if (len + add > target + 40 && parts.Count > 0) continue;
            parts.Add(l);
            len += add;
        }
        foreach (var l in parts) used.Add(IdOf(l));
        var text = string.Join(' ', parts.Select(p => p.Text));
        return new TyperacePick(text, new TyperaceSource(Chat, null, Signature(parts), null), [.. parts.Select(IdOf)]);
    }

    static string IdOf(TyperaceChatLine l) => "chat:" + l.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>«— владік, 16.09 · Smaug, 26.09 і ще 2» — хто це сказав.</summary>
    public static string Signature(IReadOnlyList<TyperaceChatLine> parts)
    {
        var shown = parts.Take(3).Select(p => p.Date.Length > 0 ? $"{p.Nick}, {p.Date}" : p.Nick);
        var more = parts.Count > 3 ? $" і ще {parts.Count - 3}" : "";
        return "— " + string.Join(" · ", shown) + more;
    }

    /// <summary>Прогрів банку на старті сервера: перший же заїзд «наших балачок» уже має з чого брати.</summary>
    public sealed class WarmUp(TyperaceChat chat) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) { chat.Warm(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
