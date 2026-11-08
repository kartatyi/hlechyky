using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Нічний відбій (секція Curfew). Ніки тримаємо в appsettings.Local.json, поза git: хто саме під відбоєм,
/// знає лише сервер. Файл підхоплюється наживо, тож список, ігри й години міняються без рестарту.
/// </summary>
public sealed class CurfewOptions
{
    /// <summary>Ніки акаунтів, яким уночі зачинено ці ігри. Регістр не має значення. Порожньо — відбою ні для кого.</summary>
    public List<string> Nicks { get; set; } = [];
    /// <summary>
    /// Які ігри зачиняються — id через кому. Рядком, а не списком: список прив'язка конфігурації дописала б
    /// до типового, а не замінила б його.
    /// </summary>
    public string Games { get; set; } = "clicker";
    /// <summary>З котрої години й до котрої за Києвом. From більше за To — через північ (23 → 6).</summary>
    public int From { get; set; } = 0;
    public int To { get; set; } = 6;
    /// <summary>
    /// Нічна година: скільки хвилин активної гри за ніч у цих іграх (рахує PlayClock, як вкладка «⏱ Час»: у колі — лише
    /// поки клікаєш). Лічильник — київська доба ранку, тобто від опівночі; якщо ніч починається раніше (From більше
    /// за To), час до опівночі не рахується. 0 — ніч зачинена вся, як був відбій до 08.11.
    /// </summary>
    public int Minutes { get; set; } = 60;
    /// <summary>
    /// У які ночі відбій — дні тижня ранку, яким ніч закінчується (mon,tue,…,sun через кому). Типово будні: ночі
    /// на суботу й неділю вільні. Порожньо — щоночі.
    /// </summary>
    public string Days { get; set; } = "mon,tue,wed,thu,fri";
    /// <summary>
    /// Указ Дядька Глека: велике вікно, яке гравці зі списку побачать один раз (з будь-якого пристрою). Новий указ —
    /// нова <see cref="NoticeV"/>. Порожній текст — указу нема. Абзаци — через порожній рядок.
    /// </summary>
    public string NoticeV { get; set; } = "";
    public string NoticeTitle { get; set; } = "";
    /// <summary>Кому з <see cref="Nicks"/> показати указ. Порожньо — усім зі списку.</summary>
    public List<string> NoticeNicks { get; set; } = [];
    public string Notice { get; set; } = "";
    /// <summary>
    /// Що бачать ті, кого це стосується, коли нічна година вийшла: плашка на сайті й відмова в грі. Поки година ще
    /// є, плашка сама пише, скільки лишилось. Порожньо — загальний текст.
    /// </summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// Нічний відбій: з <see cref="CurfewOptions.From"/> до <see cref="CurfewOptions.To"/> за київським часом гравцям
/// зі списку в іграх з <see cref="CurfewOptions.Games"/> (типово — лише Гончарне коло) дано нічну годину
/// (<see cref="CurfewOptions.Minutes"/> активної гри). Вийшла — до ранку ні відкрити, ні сісти, ні почати, ні ходити.
/// Решта ігор, радіо, балачки й усі інші люди живуть як завжди. Якщо в списку колись опиниться гра на кількох,
/// партію, яку почали ще до відбою разом із кимось, кого він не стосується, дограємо — інакше заборона двох
/// зупинила б гру всім за тим столом.
/// Вийти з акаунта й грати гостем не вийде: браузер гравця зі списку дістає позначку-куку, і гість із нею вночі
/// в ці ігри не грає зовсім — нічна година лише з акаунта (інакше гостьове добро зранку переїхало б на акаунт —
/// див. Accounts.Adopt, — а кожен вихід з акаунта давав би ще одну годину).
/// </summary>
public sealed class Curfew(IOptionsMonitor<CurfewOptions> options, IDataProtectionProvider protection, IClock clock, IPlayTime played)
{
    public const string Cookie = "hlechyky_night";

    /// <summary>Скільки тримаємо пораховану нічну годину: відмова питає на кожен клік кола, а база змінюється раз на хвилину.</summary>
    public static readonly TimeSpan UsedFresh = TimeSpan.FromSeconds(10);

    CurfewOptions O => options.CurrentValue;
    IDataProtector Protector => protection.CreateProtector("hlechyky.night");

    readonly object _lock = new();
    readonly Dictionary<string, (DateTimeOffset At, int Seconds)> _used = [];

    /// <summary>Нічна година словами: «година», «2 год», «30 хв».</summary>
    string Hour => O.Minutes % 60 == 0 ? (O.Minutes == 60 ? "година" : $"{O.Minutes / 60} год") : $"{O.Minutes} хв";

    /// <summary>Що кажемо гравцеві, коли нічна година вийшла, — і на плашці, і у відмові.</summary>
    public string Text => !string.IsNullOrWhiteSpace(O.Text) ? O.Text.Trim()
        : O.Minutes > 0
            ? $"Пора спати! Нічна година вийшла: у будні з {O.From:00}:00 до {O.To:00}:00 Гончарне коло для тебе — лише {Hour} гри за ніч. Це нічне правило лише для кількох гравців — решта грає як звичайно."
            : $"Пора спати! У будні з {O.From:00}:00 до {O.To:00}:00 Гончарне коло для тебе зачинене. Це нічне правило лише для кількох гравців — решта грає як звичайно.";

    /// <summary>Гість у браузері, де заходив хтось зі списку: уночі коло лише з акаунта.</summary>
    public const string GuestText = "Уночі Гончарне коло в цьому браузері — лише з акаунта: увійди, і Глек порахує твою нічну годину.";

    string Refused(string? nick) => "🌙 " + (Listed(nick) ? Text : GuestText);

    /// <summary>Чи зачиняє відбій цю гру.</summary>
    public bool Covers(string? gameId) =>
        !string.IsNullOrEmpty(gameId) && GameIds.Contains(gameId, StringComparer.OrdinalIgnoreCase);

    string[] GameIds => O.Games.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
    public DateTimeOffset? NightStart() => NightStart(clock.UtcNow, O.From, O.To, O.Days);

    static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>Дні тижня з рядка <see cref="CurfewOptions.Days"/>; null — щоночі.</summary>
    public static HashSet<DayOfWeek>? ParseDays(string? days)
    {
        var set = new HashSet<DayOfWeek>();
        foreach (var d in (days ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = Array.FindIndex(DayNames, n => d.StartsWith(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) set.Add((DayOfWeek)i);
        }
        return set.Count == 0 ? null : set;
    }

    /// <param name="days">Ночі, коли діє відбій, — за днем ранку, яким ніч закінчується; null чи порожньо — щоночі.</param>
    public static DateTimeOffset? NightStart(DateTimeOffset now, int from, int to, string? days = null)
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
        var morning = from < to ? day : day.AddDays(1);
        if (ParseDays(days) is { } on && !on.Contains(morning.DayOfWeek)) return null;   // вихідна ніч
        var start = day.AddHours(from);
        if (Days.Kyiv.IsInvalidTime(start)) start = start.AddHours(1);   // година, якої нема через перехід на літній час
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, Days.Kyiv), TimeSpan.Zero);
    }

    /// <summary>
    /// Скільки секунд нічної години гравець уже витратив: активний час у зачинених іграх за київську добу ранку
    /// (від опівночі). Вечірня частина ночі до опівночі (From більше за To) не рахується. Тримаємо
    /// <see cref="UsedFresh"/>: відмову питають на кожен клік.
    /// </summary>
    public int Used(string? nick)
    {
        var now = clock.UtcNow;
        var key = Auth.NickKey(nick);
        if (key.Length == 0) return 0;
        lock (_lock)
            if (_used.TryGetValue(key, out var hit) && now >= hit.At && now - hit.At < UsedFresh) return hit.Seconds;
        var seconds = 0;
        if (!(O.From > O.To && TimeZoneInfo.ConvertTime(now, Days.Kyiv).Hour >= O.From))
        {
            var day = Days.Of(now);
            foreach (var game in GameIds) seconds += played.Seconds(nick!, PlayClock.Game(game), day);
        }
        lock (_lock) _used[key] = (now, seconds);
        return seconds;
    }

    /// <summary>
    /// Скільки секунд нічної години лишилось цьому гравцеві просто зараз; null — зараз не ніч або відбій його не
    /// стосується. Гостеві в позначеному браузері й за <see cref="CurfewOptions.Minutes"/> = 0 — одразу 0.
    /// </summary>
    public int? Left(string? nick, HttpContext? http)
    {
        if (NightStart() is null || !Applies(nick, http)) return null;
        if (O.Minutes <= 0 || !Listed(nick)) return 0;
        return Math.Max(0, O.Minutes * 60 - Used(nick));
    }

    /// <summary>Відкрити, сісти, почати, ще раз у грі <paramref name="gameId"/>: текст відмови, якщо цьому гравцеві зараз не можна; null — можна.</summary>
    public string? Refusal(string nick, HttpContext? http, string? gameId) =>
        Covers(gameId) && Left(nick, http) == 0 ? Refused(nick) : null;

    /// <summary>Хід чи ввід за столом <paramref name="room"/>. Те саме, що <see cref="Refusal"/>, але партію до ночі з іншими дограємо.</summary>
    public string? MoveRefusal(string nick, HttpContext? http, Room? room)
    {
        if (room is null || !Covers(room.Info.Id) || NightStart() is not { } start || Left(nick, http) != 0) return null;
        lock (room.Sync)
            if (Finishing(room.Status, room.Info.Solo, room.StartedAt, room.Seats, start)) return null;
        return Refused(nick);
    }

    /// <summary>Партія, яку дограємо: іде, не соло, почалась до ночі, і за столом є хтось, кого відбій не стосується.</summary>
    public bool Finishing(RoomStatus status, bool solo, DateTimeOffset? startedAt, IEnumerable<string?> seats, DateTimeOffset nightStart) =>
        status == RoomStatus.Playing && !solo && startedAt < nightStart && seats.Any(s => s is not null && !Listed(s));

    /// <summary>
    /// Для /api/me і /api/night: тим, кого відбій стосується, — години, нічна година й текст плашки (котра зараз
    /// година, браузер рахує сам, щоб плашка з'являлась опівночі без перезавантаження; скільки лишилось — <c>left</c>,
    /// секунди, null — зараз не ніч, плашка вночі перепитує /api/night); решті — null, про чужий відбій їм знати
    /// нічого. Заодно ставить браузерові гравця зі списку позначку на рік.
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
        if (!Applies(nick, c)) return null;
        var listed = Listed(nick);
        return new
        {
            from = O.From, to = O.To, text = listed ? Text : GuestText,
            minutes = listed ? Math.Max(0, O.Minutes) : 0,   // 0 — нічної години нема, ніч зачинена вся
            left = Left(nick, c),
            days = ParseDays(O.Days)?.Select(d => (int)d).Order().ToArray(),   // 0 — неділя, як getDay() у браузері; null — щоночі
            notice = listed && NoticeFor(nick) && !string.IsNullOrWhiteSpace(O.Notice) && !string.IsNullOrWhiteSpace(O.NoticeV)
                ? new { v = O.NoticeV.Trim(), title = O.NoticeTitle.Trim(), text = O.Notice.Trim() } : null,
        };
    }

    bool NoticeFor(string nick) => O.NoticeNicks.Count == 0 || O.NoticeNicks.Any(n => Auth.NickKey(n) == Auth.NickKey(nick));

    /// <summary>Позначка чинна, лише поки той, на кого її поставили, досі в списку.</summary>
    bool Marked(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(Cookie, out var raw) || string.IsNullOrEmpty(raw)) return false;
        try { return Listed(Protector.Unprotect(raw)); }
        catch { return false; }   // чужа чи протухла кука
    }
}
