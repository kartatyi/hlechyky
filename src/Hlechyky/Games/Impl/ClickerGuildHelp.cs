using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Що зроблено для друга — рядок стрічки цеху («Smaug → Микола: підмайстер») і пам'ять «з ким нещодавно
/// взаємодіяв» для порядку друзів у картці. <see cref="Kind"/>: treat, lend, cheer, gift, toloka, thanks.
/// </summary>
public sealed record GuildDeed(string From, string To, string Kind, DateTimeOffset At);

/// <summary>
/// Дзвоник цеху (12-те дошліфування): «тобі щось прийшло» — на всі вкладки отримувача, щоб його відкрите коло
/// саме спитало вид (дія look) і забрало пошту, а не чекало його власного кліку. Сервер лишається головним: пошту
/// однаково забирає лише дія, а дзвоник — тільки підказка «спитай».
/// </summary>
public interface IClickerGuildWire
{
    /// <summary>Чи гончар зараз на сайті (хоч одна жива вкладка).</summary>
    bool Online(string nick);
    /// <summary>Отримувачеві щось поклали в скриньку цеху.</summary>
    void Mail(string nick, string kind, string from);
}

/// <summary>Дзвоник через хаб радіо: подія <c>clkMail</c> на всі з'єднання ніка (як «💡» у записках).</summary>
public sealed class HubClickerGuildWire(IHubContext<RadioHub> hub, Presence presence, ILogger<HubClickerGuildWire> log) : IClickerGuildWire
{
    public bool Online(string nick) => presence.IsOnline(nick);

    public void Mail(string nick, string kind, string from)
    {
        var ids = presence.ConnectionsOf(nick);
        if (ids.Count > 0) _ = SendAsync(ids, new { kind, from });
    }

    async Task SendAsync(IReadOnlyList<string> ids, object payload)
    {
        try { await hub.Clients.Clients(ids).SendAsync("clkMail", payload); }
        catch (Exception ex) { log.LogWarning(ex, "дзвоник цеху не долетів"); }
    }
}

public sealed partial class ClickerGuildService
{
    /// <summary>
    /// Другий підмайстер (від іншого друга чи завтра) ПРОДОВЖУЄ гостювання, а не перезаписує: +доба до залишку, але
    /// наперед — не більше трьох діб. Похвала так само: +година, не більше чотирьох годин залишку.
    /// </summary>
    public const int LendCapHours = 72;
    public const int CheerCapMinutes = 240;
    /// <summary>Безплатних «дякую» на день від одного гончаря (кожному другові — раз).</summary>
    public const int ThanksPerDay = 10;
    /// <summary>Дякувати можна тому, хто помагав тобі за останні стільки годин.</summary>
    public const int ThanksWindowHours = 48;
    /// <summary>Стрічка цеху: стільки останніх справ тримаємо в стані й стільки показуємо за сьогодні.</summary>
    public const int DeedsKept = 80, DeedsShown = 15;
    /// <summary>Друзі, чиї збереження читаємо для картки й «Хто чекає»: ті, хто заходив за останні дні, не більше стількох.</summary>
    public const int LookDays = 3, LookMax = 12;

    sealed partial class State
    {
        /// <summary>Стрічка цеху (12-те дошліфування). У старому стані її нема — порожня.</summary>
        public List<GuildDeed> Deeds { get; set; } = [];
    }

    IClickerGuildWire? _wire;

    /// <summary>Дзвоник і «хто онлайн» (DI дає хабовий; у тестах — свій або жодного).</summary>
    public IClickerGuildWire? GuildWire { get => _wire; set => _wire = value; }

    static void NormalizeHelp(State s)
    {
        s.Deeds = (s.Deeds ?? []).Where(d => d is { From.Length: > 0, To.Length: > 0, Kind.Length: > 0 }).ToList();
        foreach (var h in s.Helps.Values) h.Thanked = h.Thanked?.Where(x => x is { Length: > 0 }).ToList() ?? [];
    }

    /// <summary>Записати справу в стрічку (під замком). Старше двох діб і понад <see cref="DeedsKept"/> — геть.</summary>
    static void Deed(State s, string from, string to, string kind, DateTimeOffset now)
    {
        s.Deeds.Add(new GuildDeed(from.Trim(), to.Trim(), kind, now));
        var old = now.AddHours(-ThanksWindowHours);
        s.Deeds.RemoveAll(d => d.At < old);
        if (s.Deeds.Count > DeedsKept) s.Deeds.RemoveRange(0, s.Deeds.Count - DeedsKept);
    }

    /// <summary>Дзвоник отримувачеві — поза замком: хаб не мусить чекати на цех і навпаки.</summary>
    void Ring(string toNick, string kind, string fromNick)
    {
        try { _wire?.Mail(toNick.Trim(), kind, fromNick.Trim()); }
        catch (Exception ex) { _log?.LogWarning(ex, "дзвоник цеху для {Nick} не задзвонив", toNick); }
    }

    /// <summary>Чи є щось у скриньках гончаря (допомога, дарунки, толока) — для перевірок і дзвоника.</summary>
    public bool HasMail(string nickKey)
    {
        lock (_lock)
        {
            var s = S();
            return (s.Boosts.TryGetValue(nickKey, out var b) && b.Count > 0)
                || (s.Mail.TryGetValue(nickKey, out var m) && m.Count > 0)
                || (s.Toloka.TryGetValue(nickKey, out var t) && t.Count > 0);
        }
    }

    // ---------- бафи друга: що вже гріє і до котрої ----------

    /// <summary>Підмайстер і похвала зі збереження друга (поле guild.buffs). Нема — default.</summary>
    internal static (DateTimeOffset Lend, DateTimeOffset Cheer, DateTimeOffset Seen) BuffsOfSave(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return default;
            var b = o["guild"]?["buffs"];
            return (Date(b?["lend"]), Date(b?["cheer"]), Date(o["lastSync"]));
        }
        catch (JsonException) { return default; }
        catch (InvalidOperationException) { return default; }
    }

    static DateTimeOffset Date(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, out var d) ? d : default;

    /// <summary>
    /// До котрої гріє підмайстер і похвала друга з урахуванням того, що вже лежить у його скриньці (воно додасться,
    /// щойно він зайде). Під замком. Стелі — ті самі, що й у кімнаті отримувача.
    /// </summary>
    (DateTimeOffset Lend, DateTimeOffset Cheer) BuffsAheadLocked(State s, string key, DateTimeOffset lend, DateTimeOffset cheer, DateTimeOffset now)
    {
        var l = lend > now ? lend : now;
        var c = cheer > now ? cheer : now;
        if (s.Boosts.TryGetValue(key, out var box))
            foreach (var b in box)
            {
                if (b.Kind == "lend") l = Clamp(l.AddMinutes(b.Minutes), now.AddHours(LendCapHours));
                else if (b.Kind == "cheer") c = Clamp(c.AddMinutes(b.Minutes), now.AddMinutes(CheerCapMinutes));
            }
        return (l, c);
    }

    static DateTimeOffset Clamp(DateTimeOffset at, DateTimeOffset max) => at > max ? max : at;

    /// <summary>
    /// Що дарувальник бачить про друга в картці: чи він тут, коли був, скільки гостинців ще влізе, чи можна
    /// підмайстра й похвалу (і до котрої тоді продовжиться), чи вже дякував, толока (поточний етап і наступний).
    /// <paramref name="json"/> — збереження друга, прочитане поза замком.
    /// </summary>
    object LookLocked(State s, string meKey, string key, string nick, string? json, object? toloka, DateTimeOffset now)
    {
        var day = Days.Of(now);
        var mine = s.Helps.TryGetValue(meKey, out var h) && h.Day == day ? h : null;
        var got = s.Treats.TryGetValue(key, out var t) && t.Day == day ? t.Minutes : 0;
        var (lendSaved, cheerSaved, seen) = BuffsOfSave(json);
        var (lend, cheer) = BuffsAheadLocked(s, key, lendSaved, cheerSaved, now);
        var lendFull = lend >= now.AddHours(LendCapHours);
        var cheerFull = cheer >= now.AddMinutes(CheerCapMinutes);
        var cheered = mine?.Cheer.Contains(key, StringComparer.Ordinal) == true;
        DateTimeOffset? last = null;
        foreach (var d in s.Deeds)
        {
            var a = Key(d.From);
            var b = Key(d.To);
            if ((a == meKey && b == key) || (a == key && b == meKey)) last = last is { } x && x > d.At ? x : d.At;
        }
        var helpedMe = s.Deeds.Any(d => Key(d.From) == key && Key(d.To) == meKey && d.Kind != "thanks" && d.At >= now.AddHours(-ThanksWindowHours));
        return new
        {
            online = _wire?.Online(nick) ?? false,
            seenAt = seen == default ? (DateTimeOffset?)null : seen,
            treatLeft = Math.Max(0, TreatCapMinutes - got),
            lend = new
            {
                // Чи можу я сьогодні (підмайстер один на день) і чи ще влізе в стелю наперед.
                can = mine is null || !mine.Lend,
                full = lendFull,
                until = lend > now ? lend : (DateTimeOffset?)null,
                after = lendFull ? (DateTimeOffset?)null : Clamp(lend.AddHours(LendHours), now.AddHours(LendCapHours)),
            },
            cheer = new
            {
                can = !cheered,
                full = cheerFull,
                until = cheer > now ? cheer : (DateTimeOffset?)null,
                after = cheerFull ? (DateTimeOffset?)null : Clamp(cheer.AddMinutes(CheerMinutes), now.AddMinutes(CheerCapMinutes)),
            },
            thank = helpedMe && mine?.Thanked.Contains(key, StringComparer.Ordinal) != true,
            lastAt = last,
            toloka,
        };
    }

    /// <summary>
    /// Картка друга для <paramref name="meNick"/>: читаємо збереження друга (поза замком), толоку з наступним етапом,
    /// а далі — <see cref="LookLocked"/>. null — нема кому (сам собі, порожній нік).
    /// </summary>
    public object? Look(string? meNick, string? nick, DateTimeOffset now)
    {
        var me = Key(meNick);
        var key = Key(nick);
        if (key.Length == 0 || key == me) return null;
        var json = SaveOf(key);
        var toloka = json is { Length: > 0 } ? TolokaView(key, json, now) : null;
        lock (_lock)
        {
            var s = S();
            var display = s.Potters.TryGetValue(key, out var p) ? p.Nick : (nick ?? "").Trim();
            return LookLocked(s, me, key, display, json, toloka, now);
        }
    }

    /// <summary>Стрічка цеху за сьогодні — найсвіжіше першим (під замком).</summary>
    static List<object> FeedLocked(State s, DateTimeOffset now)
    {
        var day = Days.Of(now);
        // Справи дописуються за часом, тож найсвіжіше — з кінця (і за однакової миті — те, що записане пізніше).
        return Enumerable.Reverse(s.Deeds).Where(d => Days.Of(d.At) == day).Take(DeedsShown)
            .Select(d => (object)new { from = d.From, to = d.To, kind = d.Kind, at = d.At }).ToList();
    }

    // ---------- «Подякувати» ----------

    /// <summary>
    /// Подякувати тому, хто тобі помагав (за останні дві доби). Якщо сьогодні його ще не хвалив — дякуєш похвалою
    /// (справжній +10 % на годину); інакше — безплатне «дякую»: лише тост і рядок у стрічці, без впливу на економіку.
    /// Кожному — раз на день, усього <see cref="ThanksPerDay"/>. Вертає (відмова, чи це була похвала).
    /// </summary>
    public (string? Error, bool Cheer) Thank(string fromKey, string fromNick, string toNick, DateTimeOffset now)
    {
        var toKey = Key(toNick);
        var who = toNick.Trim();
        if (toKey.Length == 0) return ("Кому дякувати? Обери гончаря", false);
        if (toKey == fromKey) return ("Собі дякувати — то вже самохвальство 🙂", false);
        bool cheer;
        lock (_lock)
        {
            var s = S();
            var day = Days.Of(now);
            var help = HelpRowFor(s, fromKey, day);
            if (!s.Deeds.Any(d => Key(d.From) == toKey && Key(d.To) == fromKey && d.Kind != "thanks" && d.At >= now.AddHours(-ThanksWindowHours)))
                return ($"{who} тобі останнім часом нічого не надсилав(ла) — дякувати поки нема за що", false);
            if (help.Thanked.Contains(toKey, StringComparer.Ordinal)) return ($"Ти вже подякував(ла) {who} сьогодні", false);
            if (help.Thanked.Count >= ThanksPerDay) return ("На сьогодні досить подяк — завтра ще", false);
            cheer = !help.Cheer.Contains(toKey, StringComparer.Ordinal);
            if (!cheer)
            {
                help.Thanked.Add(toKey);
                if (!s.Boosts.TryGetValue(toKey, out var box)) s.Boosts[toKey] = box = [];
                box.Add(new GuildBoost(fromNick.Trim(), "thanks", 0, now));
                if (box.Count > MailMax) box.RemoveRange(0, box.Count - MailMax);
                Deed(s, fromNick, who, "thanks", now);
                Save();
            }
        }
        if (!cheer)
        {
            Ring(who, "thanks", fromNick);
            return (null, false);
        }
        // Похвала-подяка: звичайна похвала з усіма її межами (стеля наперед, раз на друга), і позначка «подякував».
        if (Boost(fromKey, fromNick, toNick, "cheer", 0, now) is { } why) return (why, true);
        lock (_lock)
        {
            HelpRowFor(S(), fromKey, Days.Of(now)).Thanked.Add(toKey);
            Save();
        }
        return (null, true);
    }
}
