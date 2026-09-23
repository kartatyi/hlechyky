using Hlechyky.Games;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Нічний відбій (секція Curfew). Ніки тримаємо в appsettings.Local.json, поза git: хто саме під відбоєм,
/// знає лише сервер. Файл підхоплюється наживо, тож список і години міняються без рестарту.
/// </summary>
public sealed class CurfewOptions
{
    /// <summary>Ніки акаунтів, яким уночі не можна в ігри. Регістр не має значення. Порожньо — відбою ні для кого.</summary>
    public List<string> Nicks { get; set; } = [];
    /// <summary>З котрої години й до котрої за Києвом. From більше за To — через північ (23 → 6).</summary>
    public int From { get; set; } = 0;
    public int To { get; set; } = 6;
    /// <summary>Що бачать ті, кого це стосується: плашка на сайті й відмова за столом. Порожньо — загальний текст.</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// Нічний відбій: з <see cref="CurfewOptions.From"/> до <see cref="CurfewOptions.To"/> за київським часом гравцям
/// зі списку не можна за стіл — ні відкрити соло, ні сісти, ні почати, ні ходити. Радіо, балачки й усі інші люди
/// живуть як завжди. Один виняток — партія, яку почали ще до відбою разом із кимось, кого він не стосується: її
/// дограємо, інакше заборона двох зупинила б гру всім за тим столом.
/// Вийти з акаунта й грати гостем не вийде: браузер гравця зі списку дістає позначку-куку, і гість із нею вночі
/// теж не грає (інакше гостьове добро зранку переїхало б на акаунт — див. Accounts.Adopt).
/// </summary>
public sealed class Curfew(IOptionsMonitor<CurfewOptions> options, IDataProtectionProvider protection, IClock clock)
{
    public const string Cookie = "hlechyky_night";

    CurfewOptions O => options.CurrentValue;
    IDataProtector Protector => protection.CreateProtector("hlechyky.night");

    /// <summary>Що кажемо гравцеві під відбоєм — і на плашці, і у відмові.</summary>
    public string Text => string.IsNullOrWhiteSpace(O.Text)
        ? $"Пора спати! З {O.From:00}:00 до {O.To:00}:00 ігри для тебе закриті. Це нічне правило лише для кількох гравців — решта грає як звичайно."
        : O.Text.Trim();

    string Refused => "🌙 " + Text;

    /// <summary>Чи стоїть нік у списку (котра зараз година — байдуже).</summary>
    public bool Listed(string? nick)
    {
        var key = Auth.NickKey(nick);
        return key.Length > 0 && O.Nicks.Any(n => Auth.NickKey(n) == key);
    }

    /// <summary>Кого відбій стосується: нік зі списку або гість у браузері, де вже заходив хтось зі списку.</summary>
    public bool Applies(string? nick, HttpContext? http) =>
        Listed(nick) || (http is not null && Auth.IsGuestNick(nick) && Marked(http));

    /// <summary>Коли почалась теперішня ніч (UTC); null — зараз не відбій.</summary>
    public DateTimeOffset? NightStart() => NightStart(clock.UtcNow, O.From, O.To);

    public static DateTimeOffset? NightStart(DateTimeOffset now, int from, int to)
    {
        if (from == to) return null;
        var local = TimeZoneInfo.ConvertTime(now, Days.Kyiv);
        var h = local.Hour;
        DateTime day;
        if (from < to)
        {
            if (h < from || h >= to) return null;
            day = local.Date;
        }
        else if (h >= from) day = local.Date;
        else if (h < to) day = local.Date.AddDays(-1);
        else return null;
        var start = day.AddHours(from);
        if (Days.Kyiv.IsInvalidTime(start)) start = start.AddHours(1);   // година, якої нема через перехід на літній час
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, Days.Kyiv), TimeSpan.Zero);
    }

    /// <summary>Сісти, відкрити, почати, ще раз: текст відмови, якщо цьому гравцеві зараз не можна; null — можна.</summary>
    public string? Refusal(string nick, HttpContext? http) =>
        NightStart() is not null && Applies(nick, http) ? Refused : null;

    /// <summary>Хід чи ввід за столом <paramref name="room"/>. Те саме, що <see cref="Refusal"/>, але партію до ночі з іншими дограємо.</summary>
    public string? MoveRefusal(string nick, HttpContext? http, Room? room)
    {
        if (NightStart() is not { } start || !Applies(nick, http)) return null;
        if (room is not null)
            lock (room.Sync)
                if (Finishing(room.Status, room.Info.Solo, room.StartedAt, room.Seats, start)) return null;
        return Refused;
    }

    /// <summary>Партія, яку дограємо: іде, не соло, почалась до ночі, і за столом є хтось, кого відбій не стосується.</summary>
    public bool Finishing(RoomStatus status, bool solo, DateTimeOffset? startedAt, IEnumerable<string?> seats, DateTimeOffset nightStart) =>
        status == RoomStatus.Playing && !solo && startedAt < nightStart && seats.Any(s => s is not null && !Listed(s));

    /// <summary>
    /// Для /api/me: тим, кого відбій стосується, — години й текст плашки (котра зараз година, браузер рахує сам,
    /// щоб плашка з'являлась опівночі без перезавантаження); решті — null, про чужий відбій їм знати нічого.
    /// Заодно ставить браузерові гравця зі списку позначку на рік.
    /// </summary>
    public object? ForMe(HttpContext c)
    {
        var nick = Auth.Nick(c);
        if (Auth.IsUser(c) && Listed(nick) && !Marked(c))
            c.Response.Cookies.Append(Cookie, Protector.Protect(Auth.NickKey(nick)), new CookieOptions
            {
                HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddDays(365),
            });
        return Applies(nick, c) ? new { from = O.From, to = O.To, text = Text } : null;
    }

    /// <summary>Позначка чинна, лише поки той, на кого її поставили, досі в списку.</summary>
    bool Marked(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(Cookie, out var raw) || string.IsNullOrEmpty(raw)) return false;
        try { return Listed(Protector.Unprotect(raw)); }
        catch { return false; }   // чужа чи протухла кука
    }
}
