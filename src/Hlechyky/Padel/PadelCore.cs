using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky.Padel;

// «Падельня» — падел для компанії: табло, турніри, збори, гроші за корт, рейтинг. Спільне для обох половин:
// гри (PadelPlay*: табло, турніри, гравці) і грошей (PadelMoney*: збори, витрати, банки, рейтинг і відзнаки).
// Контракт — D:/or-wt/_tools/padel-contract.md.

/// <summary>
/// Хто грає. Акаунт — <c>u:&lt;ключ ніка&gt;</c> (той самий <see cref="Auth.NickKey"/>, що в гаманців), гість падела —
/// <c>g:&lt;число&gt;</c> (просто ім'я, без акаунта; потім його можна прив'язати до акаунта, і вся історія переїде).
/// Сайтовий «гість Вася» гравцем-акаунтом не буває: у падел його вписують як гостя падела.
/// </summary>
public static class Pid
{
    public static string User(string nick) => "u:" + Auth.NickKey(nick);
    public static string Guest(long id) => "g:" + id;
    public static bool IsUser(string? pid) => pid is not null && pid.StartsWith("u:", StringComparison.Ordinal) && pid.Length > 2;
    public static bool IsGuest(string? pid) => pid is not null && pid.StartsWith("g:", StringComparison.Ordinal) && long.TryParse(pid[2..], out _);
    public static bool Valid(string? pid) => IsUser(pid) || IsGuest(pid);
    /// <summary>Ключ ніка акаунта; для гостя — null.</summary>
    public static string? NickKey(string? pid) => IsUser(pid) ? pid![2..] : null;
    /// <summary>pid того, хто прийшов із запитом (лише акаунт; гість сайту — null).</summary>
    public static string? Of(HttpContext c) => Auth.IsUser(c) ? User(Auth.Nick(c)) : null;
}

/// <summary>Налаштування з appsettings, розділ "Padel". Ціни — гривні, цілі.</summary>
public sealed class PadelOptions
{
    /// <summary>Корт за годину (один корт). Рішення власника 01.10: 1000 грн.</summary>
    public int CourtPerHour { get; set; } = 1000;
    /// <summary>Оренда ракетки — за гру, не за годину. Рішення власника 01.10: 150 грн.</summary>
    public int RacketPrice { get; set; } = 150;
    /// <summary>Глек оголошує рахунок голосом (edge-tts, Остап).</summary>
    public bool Voice { get; set; } = true;
    /// <summary>За скільки хвилин до збору нагадати тим, хто йде і зараз на сайті.</summary>
    public int ReminderMinutes { get; set; } = 60;
    /// <summary>За скільки хвилин до кінця оренди табло каже «останній гейм».</summary>
    public int LastGameMinutes { get; set; } = 10;
}

/// <summary>Гравець для показу: <c>{ pid, name, guest }</c> на дроті.</summary>
public sealed record PadelPlayer(string Pid, string Name, bool Guest, string? LinkedTo = null);

/// <summary>Гравці падела (реалізує половина гри): імена, прив'язка гостей до акаунтів.</summary>
public interface IPadelPlayers
{
    /// <summary>Ім'я для показу: нік акаунта як є або ім'я гостя; невідомий — «?».</summary>
    string Name(string pid);
    /// <summary>Кому зараховується: прив'язаний гість → pid акаунта, решта — як є.</summary>
    string Canon(string pid);
    bool Exists(string pid);
    PadelPlayer Player(string pid);
}

/// <summary>
/// Що сталося в матчі, чого не видно з самого рахунку (рахує половина гри, переграючи журнал очок рушієм):
/// скільки вирішальних очок (золоте / star point) і тайбрейків (разом із супертайбрейком) виграла кожна команда,
/// чи був «бублик» (сет 6:0, у швидкому 4:0) і камбек (виграний сет після 1:5, у швидкому 0:3).
/// </summary>
public sealed record PadelFacts(int[] GoldenWon, int[] TieBreaksWon, bool[] Bagel, bool[] Comeback)
{
    public static PadelFacts None => new([0, 0], [0, 0], [false, false], [false, false]);
}

/// <summary>
/// Готовий результат для рейтингу й статистики. <see cref="Teams"/> — уже канонічні pid (прив'язані гості замінені).
/// Mode: "match" — сети (<see cref="Sets"/>: [[6,4],[3,6]…], супертайбрейк — окремим «сетом» з очками), "points" —
/// американо-очки (<see cref="Points"/>: [15,9]). Winner: 0, 1 або -1 (нічия). Source: "live" (табло) чи "tour".
/// </summary>
public sealed record PadelResult(string Id, DateTimeOffset At, string Source, string? TourId, string Mode,
    string[][] Teams, int[][] Sets, int[]? Points, int Winner, PadelFacts Facts);

/// <summary>Підсумок завершеного турніру: місця (одиниця — гравець або пара) і нагороди (ключ → кому).</summary>
public sealed record PadelTourResult(string Id, DateTimeOffset At, string Title, string Format,
    string[][] Ranked, IReadOnlyDictionary<string, string[]> Awards);

/// <summary>Історія для рейтингу, статистики й відзнак (реалізує половина гри; читає половина грошей).</summary>
public interface IPadelHistory
{
    /// <summary>Усі зараховані результати за часом (скасовані й незакінчені — ні).</summary>
    IReadOnlyList<PadelResult> Results();
    IReadOnlyList<PadelTourResult> Tournaments();
}

/// <summary>Найближчий збір для лобі й табло (реалізує половина грошей; без неї — порожньо).</summary>
public sealed record PadelAgendaItem(string Id, DateTimeOffset Start, string Local, double Hours, int Courts,
    string Place, int Going, int Slots, DateTimeOffset Until);

public interface IPadelAgenda
{
    IReadOnlyList<PadelAgendaItem> Upcoming(int max);
    /// <summary>Збір за id (для «годинника оренди» матчу, прив'язаного до збору).</summary>
    PadelAgendaItem? Find(string id);
    /// <summary>Хто йде на збір (канонічні pid) — щоб витрата й турнір підставили людей самі.</summary>
    IReadOnlyList<string> Going(string id);
}

public sealed class NoPadelAgenda : IPadelAgenda
{
    public IReadOnlyList<PadelAgendaItem> Upcoming(int max) => [];
    public PadelAgendaItem? Find(string id) => null;
    public IReadOnlyList<string> Going(string id) => [];
}

/// <summary>Порожня історія — поки половина гри не підключена (і для тестів грошей).</summary>
public sealed class NoPadelHistory : IPadelHistory
{
    public IReadOnlyList<PadelResult> Results() => [];
    public IReadOnlyList<PadelTourResult> Tournaments() => [];
}

/// <summary>
/// Події на дроті. Сторінка /padel/ слухає свій хаб <c>/hub/padel</c> (без присутності, радіо й балачок),
/// лобі сайту — головний хаб, лише легкий пінг <c>padelLive</c>.
/// </summary>
public interface IPadelWire
{
    /// <summary>Повний вид матчу (табло) — усім на /padel/.</summary>
    void Match(object view);
    void Tournament(object view);
    void Gathering(object view);
    /// <summary>Пінг: гроші змінились — кожен сам перепитає свій баланс (він особистий).</summary>
    void Money();
    /// <summary>Пінг: рейтинг/статистика могли змінитись.</summary>
    void Rating();
    /// <summary>Лобі сайту: <c>padelLive</c> з тим самим, що віддає GET /api/padel/lobby.</summary>
    void Lobby(object summary);
    /// <summary>Тост людині на всіх її з'єднаннях сайту (і на /padel/, і на головній).</summary>
    void Toast(string nick, string text);
    /// <summary>Рядок у Балачки від Глека (kind "padel"), уже записаний у базу.</summary>
    void Chat(object line);
}

/// <summary>
/// Хаб сторінки /padel/: лише розсилка, методів у нього нема — дії йдуть через HTTP. Кожне з'єднання сидить у групі
/// свого ніка, щоб особисте (тост «Оля скинула тобі 300 грн») не летіло всім.
/// </summary>
public sealed class PadelHub : Hub
{
    public static string NickGroup(string nick) => "n:" + Auth.NickKey(nick);

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is not null) await Groups.AddToGroupAsync(Context.ConnectionId, NickGroup(Auth.Nick(http)));
        await base.OnConnectedAsync();
    }
}

public sealed class HubPadelWire(IHubContext<PadelHub> padel, IHubContext<RadioHub> radio, IOutbox outbox,
    ILogger<HubPadelWire> log) : IPadelWire
{
    public void Match(object view) => Send(padel, "match", view);
    public void Tournament(object view) => Send(padel, "tournament", view);
    public void Gathering(object view) => Send(padel, "gathering", view);
    public void Money() => Send(padel, "money", new { });
    public void Rating() => Send(padel, "rating", new { });
    public void Lobby(object summary) { Send(radio, "padelLive", summary); Send(padel, "lobby", summary); }
    public void Chat(object line) => Send(radio, "chat", line);

    public void Toast(string nick, string text)
    {
        // Головна сторінка — тим самим шляхом, що гаманець і Лавка; /padel/ — групою ніка на своєму хабі.
        outbox.Post(new ToastFor(nick, text, "ok"));
        _ = ToastPadelAsync(nick, text);
    }

    async Task ToastPadelAsync(string nick, string text)
    {
        try { await padel.Clients.Group(PadelHub.NickGroup(nick)).SendAsync("toast", new { text }); }
        catch (Exception ex) { log.LogWarning(ex, "Падельня не донесла тост"); }
    }

    void Send<T>(IHubContext<T> hub, string name, object payload) where T : Hub => _ = SendAsync(hub, name, payload);

    async Task SendAsync<T>(IHubContext<T> hub, string name, object payload) where T : Hub
    {
        try { await hub.Clients.All.SendAsync(name, payload); }
        catch (Exception ex) { log.LogWarning(ex, "Падельня не розіслала {Event}", name); }
    }
}
