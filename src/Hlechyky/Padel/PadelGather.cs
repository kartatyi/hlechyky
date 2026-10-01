using System.Globalization;
using Hlechyky.Games;
using Microsoft.Extensions.Options;
using static Hlechyky.Padel.PadelMoneySql;

namespace Hlechyky.Padel;

// =====================================================================================================================
// Збори на гру (контракт §3.1): «у суботу о 18:00, два корти, Позняки» — хто йде, хто в черзі, хто бере ракетку.
// Черга не зберігається окремо: усі записані лежать за порядком запису, перші slots — «йдуть», решта — «черга».
// Так звільнене місце саме дістається першому з черги, а зміна slots сама пересуває межу.
// =====================================================================================================================

public sealed record PadelGathering(long Id, DateTimeOffset Start, string Local, double Hours, int Courts, string Place,
    string Note, int Slots, string Status, string ByPid, DateTimeOffset Created)
{
    public string Key => "g" + Id;
    public DateTimeOffset Until => Start + TimeSpan.FromHours(Hours);
    public bool Open => Status == "open";
}

/// <summary>Записаний на збір: <see cref="AddedBy"/> — хто вписав (сам або друг), щоб той міг і виписати.</summary>
public sealed record PadelGoer(string Pid, bool Racket, DateTimeOffset At, string? AddedBy);

/// <summary>Сховище зборів. DDL і SQL — тут, від <see cref="Db"/> лише з'єднання на одну коротку операцію.</summary>
public sealed class PadelGatherStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS padel_gatherings(
            id INTEGER PRIMARY KEY AUTOINCREMENT, start TEXT NOT NULL, until TEXT NOT NULL, local TEXT NOT NULL,
            hours REAL NOT NULL, courts INTEGER NOT NULL, place TEXT NOT NULL, note TEXT NOT NULL, slots INTEGER NOT NULL,
            status TEXT NOT NULL, by_pid TEXT NOT NULL, created TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS padel_gatherings_start ON padel_gatherings(start);
        CREATE TABLE IF NOT EXISTS padel_going(
            gid INTEGER NOT NULL, pid TEXT NOT NULL, racket INTEGER NOT NULL, at TEXT NOT NULL, added_by TEXT,
            PRIMARY KEY(gid, pid));
        CREATE TABLE IF NOT EXISTS padel_reminders(
            gid INTEGER NOT NULL, pid TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(gid, pid));
        """;

    const string Cols = "id, start, local, hours, courts, place, note, slots, status, by_pid, created";

    readonly Db _db;

    public PadelGatherStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, Schema); });
    }

    static PadelGathering Row(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), Ts(r.GetString(1)), r.GetString(2),
        r.GetDouble(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetString(8), r.GetString(9), Ts(r.GetString(10)));

    public long Create(PadelGathering g) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO padel_gatherings(start, until, local, hours, courts, place, note, slots, status, by_pid, created)
            VALUES($s, $u, $l, $h, $c, $p, $n, $sl, $st, $b, $cr); SELECT last_insert_rowid();
            """, ("$s", Iso(g.Start)), ("$u", Iso(g.Until)), ("$l", g.Local), ("$h", g.Hours), ("$c", g.Courts), ("$p", g.Place),
            ("$n", g.Note), ("$sl", g.Slots), ("$st", g.Status), ("$b", g.ByPid), ("$cr", Iso(g.Created)));
        return (long)cmd.ExecuteScalar()!;
    });

    public void Update(PadelGathering g) => _db.With(c => Exec(c, """
        UPDATE padel_gatherings SET start=$s, until=$u, local=$l, hours=$h, courts=$c, place=$p, note=$n, slots=$sl, status=$st
        WHERE id=$id
        """, ("$s", Iso(g.Start)), ("$u", Iso(g.Until)), ("$l", g.Local), ("$h", g.Hours), ("$c", g.Courts), ("$p", g.Place),
        ("$n", g.Note), ("$sl", g.Slots), ("$st", g.Status), ("$id", g.Id)));

    public PadelGathering? Get(long id) => _db.With(c =>
        Rows(c, $"SELECT {Cols} FROM padel_gatherings WHERE id=$id", Row, ("$id", id)).FirstOrDefault());

    /// <summary>Ще не скінчились (кінець пізніше за <paramref name="now"/>), за часом старту.</summary>
    public List<PadelGathering> NotEnded(DateTimeOffset now) => _db.With(c =>
        Rows(c, $"SELECT {Cols} FROM padel_gatherings WHERE until > $now ORDER BY start, id", Row, ("$now", Iso(now))));

    /// <summary>Стартують від <paramref name="from"/> (включно) до <paramref name="to"/> — для минулих і нагадувань.</summary>
    public List<PadelGathering> Between(DateTimeOffset from, DateTimeOffset to) => _db.With(c =>
        Rows(c, $"SELECT {Cols} FROM padel_gatherings WHERE start >= $f AND start < $t ORDER BY start DESC, id DESC", Row,
            ("$f", Iso(from)), ("$t", Iso(to))));

    /// <summary>Минулі незкасовані збори, що вже скінчились, — для відзнаки «Завсідник».</summary>
    public List<PadelGathering> Ended(DateTimeOffset now) => _db.With(c =>
        Rows(c, $"SELECT {Cols} FROM padel_gatherings WHERE until <= $now AND status = 'open' ORDER BY start", Row, ("$now", Iso(now))));

    /// <summary>Усі записані за порядком запису: перші slots — «йдуть», решта — черга.</summary>
    public List<PadelGoer> Goers(long gid) => _db.With(c =>
        Rows(c, "SELECT pid, racket, at, added_by FROM padel_going WHERE gid=$g ORDER BY rowid", r =>
            new PadelGoer(r.GetString(0), r.GetInt32(1) != 0, Ts(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3)), ("$g", gid)));

    public bool Join(long gid, string pid, bool racket, DateTimeOffset at, string? addedBy) => _db.With(c =>
        Exec(c, "INSERT OR IGNORE INTO padel_going(gid, pid, racket, at, added_by) VALUES($g, $p, $r, $at, $b)",
            ("$g", gid), ("$p", pid), ("$r", racket ? 1 : 0), ("$at", Iso(at)), ("$b", addedBy)) > 0);

    public bool Leave(long gid, string pid) => _db.With(c =>
        Exec(c, "DELETE FROM padel_going WHERE gid=$g AND pid=$p", ("$g", gid), ("$p", pid)) > 0);

    public void SetRacket(long gid, string pid, bool racket) => _db.With(c =>
        Exec(c, "UPDATE padel_going SET racket=$r WHERE gid=$g AND pid=$p", ("$r", racket ? 1 : 0), ("$g", gid), ("$p", pid)));

    /// <summary>Останні різні місця (нові згори) — підказка в формі збору.</summary>
    public List<string> Places(int max) => _db.With(c =>
        Rows(c, "SELECT place FROM padel_gatherings GROUP BY lower(place) ORDER BY max(id) DESC LIMIT $m", r => r.GetString(0), ("$m", max)));

    /// <summary>Позначити нагадування; false — уже нагадували (переживає рестарт).</summary>
    public bool MarkReminded(long gid, string pid, DateTimeOffset at) => _db.With(c =>
        Exec(c, "INSERT OR IGNORE INTO padel_reminders(gid, pid, at) VALUES($g, $p, $at)", ("$g", gid), ("$p", pid), ("$at", Iso(at))) > 0);

    /// <summary>Час збору змінили — нагадати заново.</summary>
    public void ForgetReminders(long gid) => _db.With(c => Exec(c, "DELETE FROM padel_reminders WHERE gid=$g", ("$g", gid)));
}

/// <summary>
/// Збори: правила, права, черга з автопідняттям, нагадування, і <see cref="IPadelAgenda"/> для табло й лобі.
/// Лобі береться лінивим <paramref name="lobby"/>: клас лобі половини гри сам читає агенду, і пряма залежність
/// замкнула б коло в DI.
/// </summary>
public sealed class PadelGather(PadelGatherStore store, PadelMoneyStore money, IPadelPlayers players, IPadelWire wire,
    IClock clock, IOptions<PadelOptions> options, Func<IPadelLobby> lobby, ILogger<PadelGather> log) : IPadelAgenda
{
    public const int PlaceMax = 60, NoteMax = 200, SlotsMax = 48;
    // Запис/виписка — прочитати чергу, змінити, прочитати знову: між цим не має влізти інший запит, інакше тост
    // «звільнилось місце» дістанеться не тому. Під замком лише синхронна база, без await.
    readonly object _gate = new();

    public sealed record Form(string? Local, double? Hours, int? Courts, string? Place, string? Note, int? Slots);

    // ---------------------------------------------------------------- агенда

    public IReadOnlyList<PadelAgendaItem> Upcoming(int max) =>
        [.. store.NotEnded(clock.UtcNow).Where(g => g.Open).Take(Math.Max(0, max)).Select(g => Item(g, store.Goers(g.Id)))];

    public PadelAgendaItem? Find(string id) => Load(id) is { } x ? Item(x.G, x.All) : null;

    public IReadOnlyList<string> Going(string id) =>
        Load(id) is { } x ? [.. x.All.Take(x.G.Slots).Select(p => players.Canon(p.Pid)).Distinct()] : [];

    PadelAgendaItem Item(PadelGathering g, List<PadelGoer> all) =>
        new(g.Key, g.Start, g.Local, g.Hours, g.Courts, g.Place, Math.Min(all.Count, g.Slots), g.Slots, g.Until);

    /// <summary>Збір і всі записані (йдуть + черга) за "g5".</summary>
    public (PadelGathering G, List<PadelGoer> All)? Load(string? id) =>
        IdOf(id, 'g') is { } n && store.Get(n) is { } g ? (g, store.Goers(n)) : null;

    // ---------------------------------------------------------------- вид

    public object View(PadelGathering g, List<PadelGoer> all) => new
    {
        id = g.Key,
        start = g.Start.UtcDateTime,
        local = g.Local,
        hours = g.Hours,
        courts = g.Courts,
        place = g.Place,
        note = g.Note,
        slots = g.Slots,
        status = g.Status,
        by = players.Name(g.ByPid),
        byPid = g.ByPid,
        until = g.Until.UtcDateTime,
        going = all.Take(g.Slots).Select(Goer).ToList(),
        wait = all.Skip(g.Slots).Select(Goer).ToList(),
        expense = money.ExpenseFor(g.Id) is { } e ? "e" + e : null,
    };

    object Goer(PadelGoer p)
    {
        var who = players.Player(p.Pid);
        return new { pid = p.Pid, name = who.Name, guest = who.Guest, racket = p.Racket, at = p.At.UtcDateTime };
    }

    public object List()
    {
        var now = clock.UtcNow;
        var upcoming = store.NotEnded(now).Where(g => g.Open);
        // Минулі — за 30 днів; скасовані, що ще не настали, теж тут: у «найближчих» їм не місце, а знати про них треба
        var recent = store.Between(now.AddDays(-30), now.AddDays(400)).Where(g => !g.Open || g.Until <= now);
        return new
        {
            upcoming = upcoming.Select(g => View(g, store.Goers(g.Id))).ToList(),
            recent = recent.Select(g => View(g, store.Goers(g.Id))).ToList(),
        };
    }

    public object Places() => new { places = store.Places(12) };

    // ---------------------------------------------------------------- дії

    public IResult Create(PadelMoneyActor me, Form f)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        var courts = f.Courts ?? 1;
        var g = new PadelGathering(0, default, "", f.Hours ?? 1.5, courts, f.Place ?? "", f.Note ?? "", f.Slots ?? 4 * courts,
            "open", me.Pid!, clock.UtcNow);
        if (Check(ref g, f.Local, startChanged: true) is { } bad) return bad;
        PadelGathering made;
        lock (_gate)
        {
            var id = store.Create(g);
            made = g with { Id = id };
            store.Join(id, me.Pid!, false, clock.UtcNow, me.Pid);
        }
        log.LogInformation("Падельня: збір {Id} на {Local}, {Place}", made.Key, made.Local, made.Place);
        return Changed(made);
    }

    public IResult Edit(PadelMoneyActor me, string id, Form f)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        lock (_gate)
        {
            if (Load(id) is not { } x) return PadelMoneyHttp.Fail("Нема такого збору", 404);
            var (g, all) = x;
            if (!Owner(me, g)) return PadelMoneyHttp.Fail("Правити збір може той, хто його зібрав, або адмін", 403);
            if (!g.Open) return PadelMoneyHttp.Fail("Збір скасовано");
            var courts = f.Courts ?? g.Courts;
            // Місць не чіпали, а корти змінили — і місць було «по четверо на корт»: рахуємо так само для нових кортів
            var slots = f.Slots ?? (f.Courts is not null && g.Slots == 4 * g.Courts ? 4 * courts : g.Slots);
            var ng = g with { Hours = f.Hours ?? g.Hours, Courts = courts, Place = f.Place ?? g.Place, Note = f.Note ?? g.Note, Slots = slots };
            var moved = f.Local is not null && f.Local != g.Local;
            if (Check(ref ng, moved ? f.Local : g.Local, startChanged: moved) is { } bad) return bad;
            store.Update(ng);
            if (ng.Start != g.Start) store.ForgetReminders(g.Id);
            Promoted(me, ng, g.Slots, all, store.Goers(g.Id));
            return Changed(ng);
        }
    }

    public IResult Cancel(PadelMoneyActor me, string id)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        PadelGathering g;
        List<PadelGoer> all;
        lock (_gate)
        {
            if (Load(id) is not { } x) return PadelMoneyHttp.Fail("Нема такого збору", 404);
            (g, all) = x;
            if (!Owner(me, g)) return PadelMoneyHttp.Fail("Скасувати збір може той, хто його зібрав, або адмін", 403);
            if (!g.Open) return PadelMoneyHttp.Fail("Збір уже скасовано");
            g = g with { Status = "cancelled" };
            store.Update(g);
        }
        foreach (var nick in Accounts(all.Take(g.Slots), me))
            wire.Toast(nick, $"🍳 Збір на падел {PadelMoneyTime.When(g.Start)} ({g.Place}) скасовано");
        return Changed(g);
    }

    public IResult Join(PadelMoneyActor me, string id, string? pid, bool? racket)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        var who = string.IsNullOrWhiteSpace(pid) ? me.Pid! : pid.Trim();
        if (!players.Exists(who)) return PadelMoneyHttp.Fail("Нема такого гравця");
        lock (_gate)
        {
            if (Load(id) is not { } x) return PadelMoneyHttp.Fail("Нема такого збору", 404);
            var (g, all) = x;
            if (!g.Open) return PadelMoneyHttp.Fail("Збір скасовано");
            var canon = players.Canon(who);
            var there = all.FirstOrDefault(p => p.Pid == who || players.Canon(p.Pid) == canon);
            if (there is not null)
            {
                // Уже записаний — повторне «Я йду» нічого не ламає, лише міняє ракетку, якщо сказали
                if (racket is { } r && r != there.Racket) store.SetRacket(g.Id, there.Pid, r);
                return Changed(g);
            }
            store.Join(g.Id, who, racket ?? false, clock.UtcNow, me.Pid);
            return Changed(g);
        }
    }

    public IResult Leave(PadelMoneyActor me, string id, string? pid)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        lock (_gate)
        {
            if (Load(id) is not { } x) return PadelMoneyHttp.Fail("Нема такого збору", 404);
            var (g, all) = x;
            if (Find(all, pid ?? me.Pid!) is not { } there) return PadelMoneyHttp.Fail("Його й так нема в списку");
            if (!MayTouch(me, g, there)) return PadelMoneyHttp.Fail("Виписати іншого може той, хто зібрав чи вписав, або адмін", 403);
            store.Leave(g.Id, there.Pid);
            Promoted(me, g, g.Slots, all, store.Goers(g.Id));
            return Changed(g);
        }
    }

    public IResult Racket(PadelMoneyActor me, string id, string? pid, bool racket)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        lock (_gate)
        {
            if (Load(id) is not { } x) return PadelMoneyHttp.Fail("Нема такого збору", 404);
            var (g, all) = x;
            if (Find(all, pid ?? me.Pid!) is not { } there) return PadelMoneyHttp.Fail("Його нема в списку збору");
            if (!MayTouch(me, g, there)) return PadelMoneyHttp.Fail("Ракетку за іншого міняє той, хто зібрав чи вписав, або адмін", 403);
            store.SetRacket(g.Id, there.Pid, racket);
            return Changed(g);
        }
    }

    PadelGoer? Find(List<PadelGoer> all, string pid)
    {
        var canon = players.Canon(pid.Trim());
        return all.FirstOrDefault(p => p.Pid == pid.Trim()) ?? all.FirstOrDefault(p => players.Canon(p.Pid) == canon);
    }

    bool Owner(PadelMoneyActor me, PadelGathering g) => me.Admin || g.ByPid == me.Pid;

    bool MayTouch(PadelMoneyActor me, PadelGathering g, PadelGoer p) =>
        Owner(me, g) || players.Canon(p.Pid) == me.Pid || p.AddedBy == me.Pid;

    /// <summary>Хто перейшов із черги в «йдуть» — тост кожному акаунту (крім того, хто це зробив).</summary>
    void Promoted(PadelMoneyActor me, PadelGathering g, int slotsBefore, List<PadelGoer> before, List<PadelGoer> after)
    {
        var was = before.Take(slotsBefore).Select(p => p.Pid).ToHashSet();
        var up = after.Take(g.Slots).Where(p => !was.Contains(p.Pid) && before.Any(b => b.Pid == p.Pid));
        foreach (var nick in Accounts(up, me))
            wire.Toast(nick, $"🍳 Звільнилось місце — ти йдеш на падел {PadelMoneyTime.When(g.Start)}, {g.Place}");
    }

    IEnumerable<string> Accounts(IEnumerable<PadelGoer> who, PadelMoneyActor me) =>
        who.Select(p => players.Canon(p.Pid)).Where(p => Pid.IsUser(p) && p != me.Pid).Distinct().Select(p => Pid.NickKey(p)!);

    IResult Changed(PadelGathering g)
    {
        var view = View(g, store.Goers(g.Id));
        wire.Gathering(view);
        Lobby();
        return PadelMoneyHttp.Ok(new { ok = true, gathering = view });
    }

    void Lobby()
    {
        try { wire.Lobby(lobby().Summary()); }
        catch (Exception ex) { log.LogWarning(ex, "Падельня: лобі не склалось після зміни збору"); }
    }

    /// <summary>Перевірити й дорахувати поля збору (старт із київського часу, межі). null — усе гаразд.</summary>
    IResult? Check(ref PadelGathering g, string? local, bool startChanged)
    {
        if (!DateTime.TryParseExact(local?.Trim(), ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at))
            return PadelMoneyHttp.Fail("Коли граємо? Дата й час — як 2026-10-04T18:00");
        if (Days.Kyiv.IsInvalidTime(at)) return PadelMoneyHttp.Fail("Такої години того дня нема — годинник переводять");
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(at, Days.Kyiv), TimeSpan.Zero);
        if (startChanged && start < clock.UtcNow.AddHours(-1)) return PadelMoneyHttp.Fail("Збір у минулому не збереш");
        if (g.Hours < 0.5 || g.Hours > 4 || Math.Abs(g.Hours * 2 - Math.Round(g.Hours * 2)) > 1e-9)
            return PadelMoneyHttp.Fail("Скільки годин — від 0,5 до 4, крок пів години");
        if (g.Courts is < 1 or > 6) return PadelMoneyHttp.Fail("Кортів — від 1 до 6");
        if (g.Slots < 2 || g.Slots > SlotsMax) return PadelMoneyHttp.Fail($"Місць — від 2 до {SlotsMax}");
        var place = string.Join(' ', g.Place.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (place.Length is < 1 or > PlaceMax) return PadelMoneyHttp.Fail($"Де граємо? Назва місця — до {PlaceMax} символів");
        var note = g.Note.Trim();
        if (note.Length > NoteMax) return PadelMoneyHttp.Fail($"Примітка — до {NoteMax} символів");
        g = g with
        {
            Start = start, Local = at.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            Hours = Math.Round(g.Hours * 2) / 2, Place = place, Note = note,
        };
        return null;
    }

    // ---------------------------------------------------------------- нагадування

    /// <summary>
    /// Раз на хвилину: за <see cref="PadelOptions.ReminderMinutes"/> до старту — тост кожному акаунту, що йде, один раз
    /// (позначка в базі, тож рестарт не повторить). Збори, що вже почались, не нагадуються — після простою сервера
    /// ніхто не отримає «о 18:00 падел» о 18:40. Повертає, скільки тостів пішло.
    /// </summary>
    public int Remind()
    {
        var now = clock.UtcNow;
        var sent = 0;
        foreach (var g in store.Between(now, now.AddMinutes(Math.Max(1, options.Value.ReminderMinutes)).AddSeconds(1)))
        {
            if (!g.Open || g.Start <= now) continue;
            foreach (var p in store.Goers(g.Id).Take(g.Slots))
            {
                var canon = players.Canon(p.Pid);
                if (!Pid.IsUser(canon) || !store.MarkReminded(g.Id, canon, now)) continue;
                var tail = p.Racket ? "Ракетку беремо там" : "Ракетку береш?";
                wire.Toast(Pid.NickKey(canon)!, $"🍳 О {PadelMoneyTime.Hm(g.Start)} падел — {g.Place}. {tail}");
                sent++;
            }
        }
        return sent;
    }
}

/// <summary>Маршрути зборів: <c>/api/padel/gatherings…</c>, <c>/api/padel/places</c>.</summary>
public static class PadelGatherApi
{
    public sealed record JoinRequest(string? Pid, bool? Racket);
    public sealed record LeaveRequest(string? Pid);
    public sealed record RacketRequest(string? Pid, bool Racket);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/gatherings", (PadelGather g) => g.List());
        api.MapGet("/gatherings/{id}", (string id, PadelGather g) =>
            g.Load(id) is { } x ? Results.Ok(g.View(x.G, x.All)) : PadelMoneyHttp.Fail("Нема такого збору", 404));
        api.MapPost("/gatherings", (HttpContext c, PadelGather.Form f, PadelGather g) => g.Create(PadelMoneyActor.Of(c), f));
        api.MapPost("/gatherings/{id}/edit", (string id, HttpContext c, PadelGather.Form f, PadelGather g) => g.Edit(PadelMoneyActor.Of(c), id, f));
        api.MapPost("/gatherings/{id}/cancel", (string id, HttpContext c, PadelGather g) => g.Cancel(PadelMoneyActor.Of(c), id));
        api.MapPost("/gatherings/{id}/join", (string id, HttpContext c, JoinRequest? b, PadelGather g) =>
            g.Join(PadelMoneyActor.Of(c), id, b?.Pid, b?.Racket));
        api.MapPost("/gatherings/{id}/leave", (string id, HttpContext c, LeaveRequest? b, PadelGather g) =>
            g.Leave(PadelMoneyActor.Of(c), id, b?.Pid));
        api.MapPost("/gatherings/{id}/racket", (string id, HttpContext c, RacketRequest b, PadelGather g) =>
            g.Racket(PadelMoneyActor.Of(c), id, b.Pid, b.Racket));
        api.MapGet("/places", (PadelGather g) => g.Places());
    }
}
