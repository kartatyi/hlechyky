using Hlechyky.Games;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Hlechyky.Padel;

public sealed class PadelFinal
{
    public List<string[]> Podium { get; set; } = [];
    public List<PadelAward> Awards { get; set; } = [];
}

/// <summary>Турнір у базі (рядок — JSON запису). Сід — щоб розклад складався так само після рестарту й у тестах.</summary>
public sealed class PadelTourRec
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Format { get; set; } = "americano";
    public string Status { get; set; } = "live";
    public string Organizer { get; set; } = "";
    public string OrgPid { get; set; } = "";
    public int Courts { get; set; } = 1;
    public string Total { get; set; } = "24";
    public int? Minutes { get; set; }
    public double? Booking { get; set; }
    public string? Gathering { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int Seed { get; set; }
    public List<string> Players { get; set; } = [];
    public List<string[]>? Pairs { get; set; }
    public List<string>? Women { get; set; }
    public List<PadelRound> Rounds { get; set; } = [];
    public PadelFinal? Final { get; set; }

    [JsonIgnore] public string Key => "t" + Id;
    [JsonIgnore] public bool Pair => PadelTourGen.PairFormat(Format);
    [JsonIgnore] public int? TotalPoints => Total == "time" ? null : int.Parse(Total, CultureInfo.InvariantCulture);
    [JsonIgnore] public List<string[]> Units => Pair ? Pairs ?? [] : [.. Players.Select(p => new[] { p })];
    [JsonIgnore] public bool AnyScore => Rounds.Any(r => r.Matches.Any(m => m.Scored));
    public Random Rng(int k) => new(unchecked(Seed + 7919 * k));
}

public sealed record PadelTourRequest(string? Format, string? Title, string[]? Players, string[][]? Pairs, string[]? Women,
    int Courts, JsonElement? Total, int? Minutes, int? Rounds, double? Booking, string? Gathering);
public sealed record PadelScoreRequest(int Round, int Court, int? A, int? B);
public sealed record PadelCourtRequest(int Round, int Court);
public sealed record PadelRoundsRequest(int Add);

/// <summary>
/// Турніри (контракт §2.4): шість форматів, рахунки гравцями свого корту, живий матч на табло, таблиця, підсумки з
/// нагородами. Під своїм замком; у матчі ходимо лише після нього (матчі кличуть нас теж поза своїм замком).
/// </summary>
public sealed class PadelTours : IPadelTourLink
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS padel_tours(
            id INTEGER PRIMARY KEY, status TEXT NOT NULL, created_at TEXT NOT NULL, data TEXT NOT NULL);
        """;
    static readonly string[] Totals = ["16", "21", "24", "32", "time"];
    static readonly string[] Months = ["січня", "лютого", "березня", "квітня", "травня", "червня", "липня", "серпня", "вересня",
        "жовтня", "листопада", "грудня"];
    static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("uk-UA");

    readonly Db _db;
    readonly PadelPlayers _players;
    readonly PadelMatches _matches;
    readonly IPadelWire _wire;
    readonly IPadelAgenda _agenda;
    readonly IClock _clock;
    readonly PadelPing _ping;
    readonly IOptionsMonitor<SiteOptions> _site;
    readonly object _lock = new();
    /// <summary>Корти, на яких зараз відкривають живий матч (тур#корт): другий тиць чекає першого, а не відкриває свій.</summary>
    readonly HashSet<string> _opening = new(StringComparer.Ordinal);
    readonly Dictionary<long, PadelTourRec> _all = [];
    readonly ConcurrentDictionary<string, string> _org = new(StringComparer.Ordinal);
    long _next = 1;

    public PadelTours(Db db, PadelPlayers players, PadelMatches matches, IPadelWire wire, IPadelAgenda agenda, IClock clock,
        PadelPing ping, IOptionsMonitor<SiteOptions> site)
    {
        (_db, _players, _matches, _wire, _agenda, _clock, _ping, _site) = (db, players, matches, wire, agenda, clock, ping, site);
        var rows = _db.With(c =>
        {
            using (var cmd = c.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }
            using var q = c.CreateCommand();
            q.CommandText = "SELECT data FROM padel_tours ORDER BY id";
            using var r = q.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        });
        foreach (var json in rows)
        {
            if (PadelJson.Read<PadelTourRec>(json) is not { } t) continue;
            _all[t.Id] = t;
            _org[t.Key] = t.OrgPid;
            _next = Math.Max(_next, t.Id + 1);
        }
        matches.Link = this;
    }

    void Save(PadelTourRec t) => _db.With(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO padel_tours(id, status, created_at, data) VALUES($id, $s, $at, $d)
            ON CONFLICT(id) DO UPDATE SET status = excluded.status, data = excluded.data
            """;
        cmd.Parameters.AddWithValue("$id", t.Id);
        cmd.Parameters.AddWithValue("$s", t.Status);
        cmd.Parameters.AddWithValue("$at", t.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$d", PadelJson.Write(t));
        return cmd.ExecuteNonQuery();
    });

    PadelTourRec? Find(string id) =>
        id.StartsWith('t') && long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && _all.TryGetValue(n, out var t) ? t : null;

    public static string FormatName(string f) => f switch
    {
        "americano" => "Американо", "mexicano" => "Мексикано", "mixed" => "Мікст", "king" => "Король корту",
        "team" => "Командний", "groups" => "Групи й плей-оф", _ => "Турнір",
    };

    string UnitName(string[] u) => string.Join(" і ", u.Select(_players.Name));

    // ------------------------------------------------------------------ план і таблиця

    public static PadelReply PlanFor(string? format, int n, int courts, string? total, int? minutes, double? booking, int? women = null)
    {
        if (format is null || Array.IndexOf(PadelTourGen.Formats, format) < 0) return PadelReply.No("Невідомий формат");
        total ??= "24";
        if (Array.IndexOf(Totals, total) < 0) return PadelReply.No("Матч — до 16, 21, 24, 32 очок або на час");
        var p = PadelTourGen.Plan(format, Math.Max(0, n), Math.Clamp(courts, 1, 8), total, minutes, booking, women);
        return new(PlanView(p));
    }

    static object PlanView(PadelPlan p) => new
    {
        slots = p.Slots, sit = p.Sit, perRound = p.PerRound, fit = p.Fit, full = p.Full, fair = p.Fair, rec = p.Rec, avg = p.Avg, note = p.Note,
    };

    PadelPlan Plan(PadelTourRec t) => PadelTourGen.Plan(t.Format, t.Units.Count, t.Courts, t.Total, t.Minutes, t.Booking, t.Women?.Count);

    /// <summary>sum — усі відпочивають порівну; avg — ні (тоді чесно рахуємо середнє за матч); у групах — перемоги.</summary>
    static string RankBy(PadelTourRec t)
    {
        if (t.Format == "groups") return "wins";
        var units = t.Units;
        if (t.Format is "mexicano" or "king")
        {
            var sit = units.Count - PadelTourGen.SlotsFor(units.Count, t.Courts);
            return units.Count == 0 || t.Rounds.Count * sit % units.Count == 0 ? "sum" : "avg";
        }
        var sits = units.Select(u => t.Rounds.Count(r => r.Ready && u.All(r.Sit.Contains))).Distinct().Count();
        return sits <= 1 ? "sum" : "avg";
    }

    List<PadelRow> Table(PadelTourRec t, int upto) => PadelTourGen.Table(t.Units, t.Rounds.Take(upto), RankBy(t), UnitName);

    // ------------------------------------------------------------------ створення

    public PadelReply Create(PadelWho who, PadelTourRequest b)
    {
        if (who.Pid is null && !who.Admin) return PadelReply.No("Турнір заводять лише акаунти — увійди на головній", 403);
        var format = b.Format ?? "americano";
        if (Array.IndexOf(PadelTourGen.Formats, format) < 0) return PadelReply.No("Невідомий формат");
        if (b.Courts is < 1 or > 8) return PadelReply.No("Кортів — від 1 до 8");
        var total = b.Total is { ValueKind: JsonValueKind.Number } num ? num.GetInt32().ToString(CultureInfo.InvariantCulture)
            : b.Total is { ValueKind: JsonValueKind.String } str ? str.GetString() ?? "24" : "24";
        if (Array.IndexOf(Totals, total) < 0) return PadelReply.No("Матч — до 16, 21, 24, 32 очок або на час");
        if (total == "time" && b.Minutes is not (>= 10 and <= 20)) return PadelReply.No("На час — від 10 до 20 хвилин");
        bool Ok(string p) => Pid.Valid(p) && _players.Exists(p);
        if (!string.IsNullOrWhiteSpace(b.Gathering) && _agenda.Find(b.Gathering.Trim()) is null) return PadelReply.No("Нема такого збору");
        List<string> players;
        List<string[]>? pairs = null;
        List<string>? women = null;
        if (PadelTourGen.PairFormat(format))
        {
            if (b.Pairs is null || b.Pairs.Any(p => p is not { Length: 2 })) return PadelReply.No("Пари — по двоє");
            pairs = [.. b.Pairs.Select(p => p.ToArray())];
            players = [.. pairs.SelectMany(p => p)];
            if (pairs.Count < 2) return PadelReply.No("Потрібно щонайменше 2 пари");
        }
        else
        {
            players = [.. (b.Players is { Length: > 0 } ? b.Players : b.Gathering is { } g ? _agenda.Going(g) : [])];
            if (players.Count < 4) return PadelReply.No("Потрібно щонайменше 4 гравці");
        }
        if (players.Any(p => !Ok(p))) return PadelReply.No("Нема такого гравця");
        if (players.Distinct(StringComparer.Ordinal).Count() != players.Count) return PadelReply.No("Хтось записаний двічі");
        if (format == "mixed")
        {
            women = [.. (b.Women ?? []).Distinct().Where(players.Contains)];
            if (women.Count < 2 || players.Count - women.Count < 2) return PadelReply.No("Для міксту треба щонайменше дві жінки й двоє чоловіків");
        }
        var title = (b.Title ?? "").Trim();
        if (title.Length == 0)
        {
            var local = TimeZoneInfo.ConvertTime(_clock.UtcNow, Days.Kyiv);
            title = $"{FormatName(format)} {local.Day} {Months[local.Month - 1]}";
        }
        if (title.Length > 60) title = title[..60].TrimEnd();
        object view;
        PadelTourRec t;
        lock (_lock)
        {
            var id = _next++;
            t = new PadelTourRec
            {
                Id = id, Title = title, Format = format, Organizer = who.Name, OrgPid = who.Pid ?? "", Courts = b.Courts, Total = total,
                Minutes = total == "time" ? b.Minutes : null, Booking = b.Booking, Gathering = b.Gathering, CreatedAt = _clock.UtcNow,
                Seed = unchecked((int)(id * 2654435761L % int.MaxValue)), Players = players, Pairs = pairs, Women = women,
            };
            var n = Math.Clamp(b.Rounds ?? Plan(t).Rec, 1, 60);
            var rnd = t.Rng(0);
            t.Rounds = format switch
            {
                "americano" => PadelTourGen.Americano(players, t.Courts, n, rnd),
                "mixed" => PadelTourGen.Mixed(women!, [.. players.Except(women!)], t.Courts, n, rnd),
                "team" => PadelTourGen.Team(pairs!, t.Courts, n),
                "groups" => PadelTourGen.GroupRounds(pairs!, PadelTourGen.Groups(pairs!.Count), t.Courts),
                _ => [PadelTourGen.First(players, t.Courts, rnd), .. Enumerable.Range(1, n - 1).Select(_ => new PadelRound())],
            };
            Renumber(t);
            _all[id] = t;
            _org[t.Key] = t.OrgPid;
            Save(t);
            view = View(t);
        }
        Changed(view);
        return new(new { ok = true, tournament = view });
    }

    static void Renumber(PadelTourRec t)
    {
        for (var i = 0; i < t.Rounds.Count; i++) t.Rounds[i].N = i + 1;
    }

    void Changed(object view, bool rating = false)
    {
        _wire.Tournament(view);
        _ping.Ping();
        if (rating) _wire.Rating();
    }

    // ------------------------------------------------------------------ рахунок

    bool CanScore(PadelTourRec t, PadelTMatch m, PadelWho who) =>
        who.Admin || who.Pid is not null && (who.Pid == t.OrgPid || m.A.Concat(m.B).Any(p => _players.Canon(p) == who.Pid));

    static bool Playoff(PadelRound r) => r.Stage is "semi" or "final" or "third";

    public PadelReply Score(string id, PadelWho who, PadelScoreRequest b)
    {
        object view;
        lock (_lock)
        {
            if (Find(id) is not { } t) return PadelReply.No("Нема такого турніру", 404);
            if (t.Status != "live") return PadelReply.No("Турнір уже завершено");
            if (b.Round < 1 || b.Round > t.Rounds.Count || !t.Rounds[b.Round - 1].Ready) return PadelReply.No("Нема такого раунду", 404);
            var r = t.Rounds[b.Round - 1];
            if (r.Matches.FirstOrDefault(m => m.Court == b.Court) is not { } m) return PadelReply.No("Нема такого матчу", 404);
            if (!CanScore(t, m, who)) return PadelReply.No("Рахунок вносять гравці цього корту", 403);
            if (b.A is null && b.B is null) { m.Sa = m.Sb = null; m.By = null; m.At = null; }
            else
            {
                if (b.A is not { } a || b.B is not { } bb) return PadelReply.No("Потрібні обидва рахунки");
                if (a < 0 || bb < 0 || a > 999 || bb > 999) return PadelReply.No("Рахунок — від нуля");
                if (t.TotalPoints is { } tot && a + bb != tot) return PadelReply.No($"Разом має бути {tot}");
                if (Playoff(r) && a == bb) return PadelReply.No("У плей-оф нічиєї нема");
                m.Sa = a; m.Sb = bb; m.By = who.Name; m.At = _clock.UtcNow;
            }
            Advance(t);
            Save(t);
            view = View(t);
        }
        Changed(view, rating: true);
        return new(new { ok = true, tournament = view });
    }

    /// <summary>Повний раунд — наступний (мексикано, король, плей-оф). Уже сформований наступний не переробляється.</summary>
    void Advance(PadelTourRec t)
    {
        if (t.Format is "mexicano" or "king")
        {
            for (var k = 0; k + 1 < t.Rounds.Count; k++)
            {
                if (!t.Rounds[k].Done || t.Rounds[k + 1].Ready) continue;
                var st = PadelTourGen.Stats.From(t.Rounds.Take(k + 1));
                var rnd = t.Rng(k + 1);
                var table = Table(t, k + 1);
                var next = t.Format == "mexicano"
                    ? PadelTourGen.Mexicano(t.Players, t.Courts, st, [.. table.Select(r => r.Unit[0])], rnd)
                    : PadelTourGen.King(t.Rounds[k], t.Players, t.Courts, st, p => table.FirstOrDefault(r => r.Unit[0] == p)?.Diff ?? 0, rnd);
                t.Rounds[k + 1] = next;
            }
            Renumber(t);
            return;
        }
        if (t.Format != "groups") return;
        var pairs = t.Pairs!;
        var group = t.Rounds.Where(r => r.Stage == "group").ToList();
        if (!group.All(r => r.Done)) return;
        PadelTMatch M(string[] a, string[] b, string? stage) => new() { A = a, B = b, Stage = stage };
        void Add(string stage, params PadelTMatch[] ms)
        {
            var per = t.Courts >= 2 ? ms.Length : 1;
            for (var i = 0; i < ms.Length; i += per)
            {
                var chunk = ms.Skip(i).Take(per).ToList();
                for (var c = 0; c < chunk.Count; c++) chunk[c].Court = c + 1;
                var playing = chunk.SelectMany(x => x.A.Concat(x.B)).ToHashSet();
                t.Rounds.Add(new PadelRound
                {
                    Ready = true, Stage = per == 1 && chunk[0].Stage == "third" ? "third" : stage, Matches = chunk,
                    Sit = [.. t.Players.Where(p => !playing.Contains(p))],
                });
            }
        }
        string[] Winner(PadelTMatch m) => m.Sa > m.Sb ? m.A : m.B;
        string[] Loser(PadelTMatch m) => m.Sa > m.Sb ? m.B : m.A;
        var semis = t.Rounds.Where(r => r.Stage == "semi").ToList();
        var finals = t.Rounds.Where(r => r.Stage is "final" or "third").ToList();
        if (semis.Count == 0 && finals.Count == 0)
        {
            var groups = PadelTourGen.Groups(pairs.Count);
            var tables = groups.Select(g => PadelTourGen.Table([.. g.Select(i => pairs[i])], group, "wins", UnitName)).ToList();
            if (groups.Count == 2)
                Add("semi", M(tables[0][0].Unit, tables[1][1].Unit, null), M(tables[1][0].Unit, tables[0][1].Unit, null));
            else if (pairs.Count >= 4)
                Add("semi", M(tables[0][0].Unit, tables[0][3].Unit, null), M(tables[0][1].Unit, tables[0][2].Unit, null));
            else Add("final", M(tables[0][0].Unit, tables[0][1].Unit, "final"));
        }
        else if (semis.Count > 0 && semis.All(r => r.Done) && finals.Count == 0)
        {
            var sm = semis.SelectMany(r => r.Matches).ToList();
            // На одному корті — спершу за 3-тє місце, фінал — наостанок
            if (t.Courts >= 2) Add("final", M(Winner(sm[0]), Winner(sm[1]), "final"), M(Loser(sm[0]), Loser(sm[1]), "third"));
            else { Add("third", M(Loser(sm[0]), Loser(sm[1]), "third")); Add("final", M(Winner(sm[0]), Winner(sm[1]), "final")); }
        }
        Renumber(t);
    }

    // ------------------------------------------------------------------ живий матч на табло

    public PadelReply Live(string id, PadelWho who, PadelCourtRequest b)
    {
        string[][] teams;
        int? total;
        PadelTourRef tref;
        string busy;
        lock (_lock)
        {
            // Подвійний тиць «на табло» чи двоє з одного корту разом: перший відкриває, решта чекає й бере його матч
            busy = $"{id}#{b.Court}";
            var waitUntil = DateTime.UtcNow.AddSeconds(5);
            while (_opening.Contains(busy))
            {
                var left = waitUntil - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_lock, left)) return PadelReply.No("Корт саме відкривають — спробуй ще раз");
            }
            if (Find(id) is not { } t) return PadelReply.No("Нема такого турніру", 404);
            if (t.Status != "live") return PadelReply.No("Турнір уже завершено");
            if (b.Round < 1 || b.Round > t.Rounds.Count || !t.Rounds[b.Round - 1].Ready) return PadelReply.No("Нема такого раунду", 404);
            if (t.Rounds[b.Round - 1].Matches.FirstOrDefault(m => m.Court == b.Court) is not { } m) return PadelReply.No("Нема такого матчу", 404);
            if (!CanScore(t, m, who)) return PadelReply.No("Керують гравці цього корту", 403);
            if (who.Pid is null && !who.Admin) return PadelReply.No("Керувати можуть лише акаунти", 403);
            if (m.Live is { } running && _matches.View(running) is { } existing) return new(new { ok = true, match = existing });
            if (t.Rounds.SelectMany(r => r.Matches).Any(x => x.Court == b.Court && x.Live is not null))
                return PadelReply.No($"На корті {b.Court} уже йде матч");
            teams = [m.A, m.B];
            total = t.TotalPoints;
            tref = new(t.Key, b.Round, b.Court);
            _opening.Add(busy);
        }
        string key;
        object view;
        try { (key, view) = _matches.OpenForTour(who, teams, total, tref); }
        catch
        {
            lock (_lock) { _opening.Remove(busy); Monitor.PulseAll(_lock); }
            throw;
        }
        object tview;
        lock (_lock)
        {
            _opening.Remove(busy);
            Monitor.PulseAll(_lock);
            if (Find(id) is not { } t) return new(new { ok = true, match = view });
            var m = t.Rounds[tref.Round - 1].Matches.First(x => x.Court == tref.Court);
            m.Live = key;
            Save(t);
            tview = View(t);
        }
        Changed(tview);
        return new(new { ok = true, match = view });
    }

    public string? Organizer(string tourId) => _org.TryGetValue(tourId, out var o) ? o : null;

    /// <summary>Живий матч турніру скінчився: рахунок лягає сам (у плей-оф нічию не пишемо — її вносять руками).</summary>
    public void Scored(PadelMatchRec lm, string by)
    {
        object? view = null;
        lock (_lock)
        {
            if (lm.Tour is not { } tr || Find(tr.Id) is not { } t || tr.Round > t.Rounds.Count) return;
            var r = t.Rounds[tr.Round - 1];
            if (r.Matches.FirstOrDefault(x => x.Court == tr.Court) is not { } m) return;
            if (m.Live == lm.Key) m.Live = null;
            var s = lm.R.State;
            if (t.Status == "live" && !(Playoff(r) && s.Pts[0] == s.Pts[1]))
            {
                m.Sa = s.Pts[0]; m.Sb = s.Pts[1]; m.By = by; m.At = _clock.UtcNow;
                Advance(t);
            }
            Save(t);
            view = View(t);
        }
        Changed(view, rating: true);
    }

    public void Reopened(PadelMatchRec lm)
    {
        object? view = null;
        lock (_lock)
        {
            if (lm.Tour is not { } tr || Find(tr.Id) is not { } t || t.Status != "live" || tr.Round > t.Rounds.Count) return;
            if (t.Rounds[tr.Round - 1].Matches.FirstOrDefault(x => x.Court == tr.Court) is not { } m || m.Live is not null) return;
            // Рахунок лишаємо: «↶» могли тицьнути випадково — тоді наступне очко знову завершить матч і перепише його
            m.Live = lm.Key;
            Save(t);
            view = View(t);
        }
        Changed(view);
    }

    public void Dropped(PadelMatchRec lm)
    {
        object? view = null;
        lock (_lock)
        {
            if (lm.Tour is not { } tr || Find(tr.Id) is not { } t) return;
            foreach (var m in t.Rounds.SelectMany(r => r.Matches).Where(m => m.Live == lm.Key)) m.Live = null;
            Save(t);
            view = View(t);
        }
        Changed(view);
    }

    // ------------------------------------------------------------------ раунди, кінець, видалення

    public PadelReply AddRounds(string id, PadelWho who, PadelRoundsRequest b)
    {
        object view;
        lock (_lock)
        {
            if (Find(id) is not { } t) return PadelReply.No("Нема такого турніру", 404);
            if (!who.Admin && who.Pid != t.OrgPid) return PadelReply.No("Раунди додає організатор", 403);
            if (t.Status != "live") return PadelReply.No("Турнір уже завершено");
            if (t.Format == "groups") return PadelReply.No("У групах раунди складає сітка");
            var add = Math.Clamp(b.Add, 1, 10);
            for (var i = 0; i < add; i++)
            {
                var k = t.Rounds.Count;
                var st = PadelTourGen.Stats.From(t.Rounds);
                t.Rounds.Add(t.Format switch
                {
                    "americano" => PadelTourGen.RoundAmericano(t.Players, t.Courts, st, 250, t.Rng(k)),
                    "mixed" => PadelTourGen.RoundMixed(t.Women!, [.. t.Players.Except(t.Women!)], t.Courts, st, 250, t.Rng(k)),
                    "team" => PadelTourGen.Team(t.Pairs!, t.Courts, k + 1)[^1],
                    _ => new PadelRound(),
                });
            }
            Renumber(t);
            Advance(t);
            Save(t);
            view = View(t);
        }
        Changed(view);
        return new(new { ok = true, tournament = view });
    }

    public PadelReply Finish(string id, PadelWho who)
    {
        object view;
        string chat;
        lock (_lock)
        {
            if (Find(id) is not { } t) return PadelReply.No("Нема такого турніру", 404);
            if (!who.Admin && who.Pid != t.OrgPid) return PadelReply.No("Завершує організатор", 403);
            if (t.Status != "live") return PadelReply.No("Турнір уже завершено");
            if (!t.AnyScore) return PadelReply.No("Ще нема жодного рахунку");
            var table = Table(t, t.Rounds.Count);
            var podium = Podium(t, table);
            t.Final = new PadelFinal { Podium = podium, Awards = PadelTourGen.Awards(t.Rounds, table, _players.Name) };
            t.Status = "done";
            t.EndedAt = _clock.UtcNow;
            Save(t);
            view = View(t);
            string[] med = ["🥇", "🥈", "🥉"];
            var rank = RankBy(t);
            chat = $"🍳 Падельня: «{t.Title}» — " + string.Join(" · ", podium.Take(3).Select((u, i) =>
            {
                var row = table.FirstOrDefault(r => r.Unit.SequenceEqual(u));
                var value = row is null || rank == "wins" ? "" : rank == "avg" ? " " + row.Avg.ToString("0.0", Uk) : " " + row.Pts;
                return $"{med[i]} {UnitName(u)}{value}";
            }));
        }
        var line = _db.AddChat(_site.CurrentValue.DjName is { Length: > 0 } dj ? dj : "Дядько Глек", chat, "padel");
        _wire.Chat(new
        {
            id = line.Id, nick = line.Nick, text = line.Text, at = line.At, kind = line.Kind, roomId = line.RoomId,
            replyTo = line.ReplyTo, replyNick = line.ReplyNick, replyText = line.ReplyText, likes = line.Likes, topic = line.Topic,
        });
        Changed(view, rating: true);
        return new(new { ok = true, tournament = view });
    }

    /// <summary>П'єдестал: у групах — за плей-оф (фінал, за 3-тє), інакше — перші три таблиці (хто грав).</summary>
    static List<string[]> Podium(PadelTourRec t, List<PadelRow> table)
    {
        var ranked = table.Where(r => r.Pl > 0).Select(r => r.Unit).ToList();
        var fin = t.Rounds.SelectMany(r => r.Matches).FirstOrDefault(m => m.Stage == "final" && m.Scored);
        if (t.Format != "groups" || fin is null) return [.. ranked.Take(3)];
        var top = new List<string[]> { fin.Sa > fin.Sb ? fin.A : fin.B, fin.Sa > fin.Sb ? fin.B : fin.A };
        var third = t.Rounds.SelectMany(r => r.Matches).FirstOrDefault(m => m.Stage == "third" && m.Scored);
        if (third is not null) top.Add(third.Sa > third.Sb ? third.A : third.B);
        foreach (var u in ranked) if (top.Count < 3 && !top.Any(x => x.SequenceEqual(u))) top.Add(u);
        return top;
    }

    public PadelReply Delete(string id, PadelWho who)
    {
        List<string> live;
        string key;
        lock (_lock)
        {
            if (Find(id) is not { } t) return PadelReply.No("Нема такого турніру", 404);
            if (!who.Admin && who.Pid != t.OrgPid) return PadelReply.No("Видалити може організатор", 403);
            if (!who.Admin && t.AnyScore) return PadelReply.No("Уже є рахунки — видалити може лише адмін");
            live = [.. t.Rounds.SelectMany(r => r.Matches).Select(m => m.Live).OfType<string>()];
            key = t.Key;
            _all.Remove(t.Id);
            _org.TryRemove(key, out _);
            _db.With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM padel_tours WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", t.Id);
                return cmd.ExecuteNonQuery();
            });
        }
        foreach (var m in live) _matches.Drop(m);
        Changed(new { id = key, status = "deleted" }, rating: true);
        return new(new { ok = true });
    }

    // ------------------------------------------------------------------ вид і списки

    public object? View(string id)
    {
        lock (_lock) return Find(id) is { } t ? View(t) : null;
    }

    object View(PadelTourRec t)
    {
        var rows = Table(t, t.Rounds.Count);
        var k = t.Rounds.FindLastIndex(r => r.Matches.Any(m => m.Scored));
        var prev = k > 0 ? Table(t, k) : null;
        int Move(PadelRow r, int i) => prev is null ? 0 : prev.FindIndex(p => p.Unit.SequenceEqual(r.Unit)) - i;
        return new
        {
            id = t.Key, title = t.Title, format = t.Format, status = t.Status, organizer = t.Organizer, courts = t.Courts,
            total = t.Total, minutes = t.Minutes, createdAt = t.CreatedAt, gathering = t.Gathering,
            players = t.Players.Select(_players.P).ToArray(),
            pairs = t.Pairs?.Select(p => p.Select(_players.P).ToArray()).ToArray(),
            women = t.Women,
            rounds = t.Rounds.Select(r => new
            {
                n = r.N, stage = r.Stage, ready = r.Ready,
                matches = r.Matches.Select(m => new { court = m.Court, a = m.A, b = m.B, sa = m.Sa, sb = m.Sb, live = m.Live, by = m.By, stage = m.Stage, group = m.Group }).ToArray(),
                sit = r.Sit,
            }).ToArray(),
            table = rows.Select((r, i) => new
            {
                unit = r.Unit, pts = r.Pts, con = r.Con, pl = r.Pl, w = r.W, d = r.D, sat = r.Sat, avg = Math.Round(r.Avg, 1), diff = r.Diff, move = Move(r, i),
            }).ToArray(),
            rankBy = RankBy(t),
            ctl = new[] { t.OrgPid },
            plan = PlanView(Plan(t)),
            final = t.Final is { } f
                ? new { podium = f.Podium, awards = f.Awards.Select(a => new { key = a.Key, emoji = a.Emoji, title = a.Title, text = a.Text, pids = a.Pids }).ToArray() }
                : null,
        };
    }

    object Short(PadelTourRec t)
    {
        var top = Table(t, t.Rounds.Count).FirstOrDefault(r => r.Pl > 0);
        return new
        {
            id = t.Key, title = t.Title, format = t.Format, status = t.Status, players = t.Players.Count, createdAt = t.CreatedAt,
            leader = t.Final is { Podium.Count: > 0 } f ? UnitName(f.Podium[0]) : top is null ? null : UnitName(top.Unit),
        };
    }

    public object List()
    {
        lock (_lock)
            return new
            {
                live = _all.Values.Where(t => t.Status == "live").OrderByDescending(t => t.CreatedAt).Select(Short).ToArray(),
                recent = _all.Values.Where(t => t.Status == "done").OrderByDescending(t => t.EndedAt).Take(20).Select(Short).ToArray(),
            };
    }

    /// <summary>Для лобі: живі турніри з поточним раундом.</summary>
    public IReadOnlyList<object> LobbyTours()
    {
        lock (_lock)
            return [.. _all.Values.Where(t => t.Status == "live").OrderByDescending(t => t.CreatedAt).Select(t =>
            {
                var cur = t.Rounds.FindIndex(r => !r.Done);
                return (object)new { id = t.Key, title = t.Title, round = cur < 0 ? t.Rounds.Count : cur + 1, of = t.Rounds.Count };
            })];
    }

    /// <summary>Усі матчі турнірів з рахунком (для історії й списку гравців).</summary>
    public IReadOnlyList<(PadelTourRec T, PadelRound R, PadelTMatch M)> Scored()
    {
        lock (_lock)
            return [.. _all.Values.SelectMany(t => t.Rounds.SelectMany(r => r.Matches.Where(m => m.Scored).Select(m => (t, r, m))))];
    }

    /// <summary>Хто записаний у турніри (для списку гравців): pid і коли турнір заведено.</summary>
    public IReadOnlyList<(string Pid, DateTimeOffset At)> Rosters()
    {
        lock (_lock) return [.. _all.Values.SelectMany(t => t.Players.Select(p => (p, t.CreatedAt)))];
    }

    public IReadOnlyList<PadelTourResult> Done(Func<string, string> canon)
    {
        lock (_lock)
            return [.. _all.Values.Where(t => t.Status == "done" && t.Final is not null).OrderBy(t => t.EndedAt).Select(t =>
            {
                var table = Table(t, t.Rounds.Count).Where(r => r.Pl > 0).Select(r => r.Unit).ToList();
                var ranked = t.Final!.Podium.Concat(table.Where(u => !t.Final.Podium.Any(p => p.SequenceEqual(u))))
                    .Select(u => u.Select(canon).ToArray()).ToArray();
                var awards = t.Final.Awards.ToDictionary(a => a.Key, a => a.Pids.Select(canon).ToArray());
                return new PadelTourResult(t.Key, t.EndedAt ?? t.CreatedAt, t.Title, t.Format, ranked, awards);
            })];
    }
}

/// <summary>Історія для рейтингу й відзнак (половина грошей): завершені живі матчі й рахунки турнірів, усе — канонічними pid.</summary>
public sealed class PadelHistory(PadelMatches matches, PadelTours tours, IPadelPlayers players) : IPadelHistory
{
    public IReadOnlyList<PadelResult> Results()
    {
        string[][] Canon(string[][] teams) => [.. teams.Select(t => t.Select(players.Canon).ToArray())];
        var list = new List<PadelResult>();
        foreach (var m in matches.All().Where(m => m.Status == "done" && m.Tour is null && m.Counts))
        {
            var s = m.R.State;
            list.Add(new PadelResult(m.Key, m.EndedAt ?? m.LastAt, "live", null, m.Rules.Mode, Canon(m.Teams),
                [.. s.Sets.Select(x => x.G.ToArray())], m.Rules.Points ? [.. s.Pts] : null, s.Winner, m.R.Facts));
        }
        foreach (var (t, r, m) in tours.Scored())
            list.Add(new PadelResult($"{t.Key}:r{r.N}:c{m.Court}", m.At ?? t.CreatedAt, "tour", t.Key, "points", Canon([m.A, m.B]), [],
                [m.Sa!.Value, m.Sb!.Value], m.Sa == m.Sb ? -1 : m.Sa > m.Sb ? 0 : 1, PadelFacts.None));
        return [.. list.OrderBy(x => x.At)];
    }

    public IReadOnlyList<PadelTourResult> Tournaments() => tours.Done(players.Canon);
}

/// <summary>Що показує лобі сайту: живі матчі з рахунком, живі турніри, найближчий збір.</summary>
public sealed class PadelLobby(PadelMatches matches, PadelTours tours, IPadelPlayers players, IPadelAgenda agenda) : IPadelLobby
{
    public object Summary()
    {
        var next = agenda.Upcoming(1).FirstOrDefault();
        return new
        {
            live = matches.All().Where(m => m.Status == "live").OrderByDescending(m => m.CreatedAt).Select(m => new
            {
                id = m.Key, teams = m.Teams.Select(t => t.Select(players.Name).ToArray()).ToArray(),
                score = PadelScore.ScoreText(m.R.State, m.Rules), mode = m.Rules.Mode,
            }).ToArray(),
            tours = tours.LobbyTours(),
            next = next is null ? null : new { id = next.Id, local = next.Local, place = next.Place, going = next.Going, slots = next.Slots },
        };
    }
}
