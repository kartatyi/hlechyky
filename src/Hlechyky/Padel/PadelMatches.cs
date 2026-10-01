using Hlechyky.Games;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Hlechyky.Padel;

/// <summary>Хто прийшов із запитом: pid акаунта (null — гість сайту, лише дивиться), ім'я для «by», адмін.</summary>
public sealed record PadelWho(string? Pid, string Name, bool Admin)
{
    public static PadelWho Of(HttpContext c) => new(Hlechyky.Padel.Pid.Of(c), Auth.Nick(c), Auth.IsAdmin(c));
}

public sealed record PadelTourRef(string Id, int Round, int Court);

/// <summary>Що зробила остання дія: події, напис, що сказав Глек (текст; кліп шукаємо при показі), хто.</summary>
public sealed record PadelLast(int Seq, string[] Events, string Text, string? Say, string By);

/// <summary>Живий матч у базі: правила, команди, перший порядок подачі й журнал — стан з них переграється.</summary>
public sealed class PadelMatchRec
{
    public long Id { get; set; }
    public PadelRules Rules { get; set; } = new();
    public string[][] Teams { get; set; } = [];
    public string[] First { get; set; } = PadelScore.Slots;
    /// <summary>"0"/"1" — очко, "s:A1" — подавач, "f" — завершено вручну.</summary>
    public List<string> Journal { get; set; } = [];
    public string Status { get; set; } = "live";
    public string By { get; set; } = "";
    public string ByPid { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? CourtUntil { get; set; }
    public PadelTourRef? Tour { get; set; }
    public string? Gathering { get; set; }
    public int Seq { get; set; }
    public PadelLast? Last { get; set; }
    public bool Clock10 { get; set; }
    public bool Clock0 { get; set; }
    public bool Chatted { get; set; }
    public string? EndedBy { get; set; }

    [JsonIgnore] public PadelReplay R { get; set; } = null!;
    [JsonIgnore] public string Key => "m" + Id;

    public void Replay() => R = PadelScore.Replay(Rules, First, Journal);

    /// <summary>Чи це вже гра, а не випадковий тиць: хоч гейм, або ≥8 очок.</summary>
    public bool Counts => R.State.Log.Count >= 8 || R.State.TotalGames > 0 || R.State.Sets.Count > 0;

    public string SlotPid(string slot) => Teams[PadelScore.TeamOf(slot)][slot[1] - '0'];
}

/// <summary>Зв'язок із турнірами: хто організатор (права на матч) і «матч скінчився — запиши рахунок».</summary>
public interface IPadelTourLink
{
    string? Organizer(string tourId);
    void Scored(PadelMatchRec m, string by);
    void Dropped(PadelMatchRec m);
    /// <summary>«↶» після кінця: матч знову живий — корт турніру знову зайнятий ним (записаний рахунок лишається).</summary>
    void Reopened(PadelMatchRec m);
}

public sealed record PadelMatchRequest(string[][]? Teams, PadelRules? Rules, string? First, string? Gathering, DateTimeOffset? CourtUntil);
/// <summary>Seq — seq виду, який бачив клієнт: «очко» по застарілому виду (подвійний тиць, два телефони) не ляже двічі.</summary>
public sealed record PadelActRequest(string? A, int? T, string? Slot, DateTimeOffset? Until, int? Seq = null);

/// <summary>
/// Табло: живі матчі (контракт §2.3). Усе в пам'яті під одним замком, кожна дія одразу пишеться в базу (рядок —
/// JSON запису), тож рестарт нічого не губить. Розсилка, тур і балачки — уже після замка.
/// </summary>
public sealed class PadelMatches
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS padel_matches(
            id INTEGER PRIMARY KEY, status TEXT NOT NULL, tour_id TEXT, created_at TEXT NOT NULL, data TEXT NOT NULL);
        """;
    public static readonly TimeSpan Idle = TimeSpan.FromHours(6);

    readonly Db _db;
    readonly PadelPlayers _players;
    readonly IPadelWire _wire;
    readonly IPadelVoice _voice;
    readonly IPadelAgenda _agenda;
    readonly IClock _clock;
    readonly PadelPing _ping;
    readonly IOptionsMonitor<PadelOptions> _options;
    readonly IOptionsMonitor<SiteOptions> _site;
    readonly object _lock = new();
    readonly Dictionary<long, PadelMatchRec> _all = [];
    long _next = 1;

    public IPadelTourLink? Link { get; set; }

    public PadelMatches(Db db, PadelPlayers players, IPadelWire wire, IPadelVoice voice, IPadelAgenda agenda, IClock clock,
        PadelPing ping, IOptionsMonitor<PadelOptions> options, IOptionsMonitor<SiteOptions> site)
    {
        (_db, _players, _wire, _voice, _agenda, _clock, _ping, _options, _site) = (db, players, wire, voice, agenda, clock, ping, options, site);
        var rows = _db.With(c =>
        {
            using (var cmd = c.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }
            using var q = c.CreateCommand();
            q.CommandText = "SELECT data FROM padel_matches ORDER BY id";
            using var r = q.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        });
        foreach (var json in rows)
        {
            var m = PadelJson.Read<PadelMatchRec>(json);
            if (m is null) continue;
            m.Replay();
            _all[m.Id] = m;
            _next = Math.Max(_next, m.Id + 1);
        }
    }

    void Save(PadelMatchRec m) => _db.With(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO padel_matches(id, status, tour_id, created_at, data) VALUES($id, $s, $t, $at, $d)
            ON CONFLICT(id) DO UPDATE SET status = excluded.status, tour_id = excluded.tour_id, data = excluded.data
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$s", m.Status);
        cmd.Parameters.AddWithValue("$t", (object?)m.Tour?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", m.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$d", PadelJson.Write(m));
        return cmd.ExecuteNonQuery();
    });

    static long? ParseId(string id) =>
        id.StartsWith('m') && long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    string Team(PadelMatchRec m, int t) => $"{_players.Name(m.Teams[t][0])} і {_players.Name(m.Teams[t][1])}";

    /// <summary>«Smaug і Андрія» — знахідний для акаунтів (як родовий у живих), гостей не відмінюємо.</summary>
    string TeamAcc(PadelMatchRec m, int t) =>
        string.Join(" і ", m.Teams[t].Select(p => Pid.IsUser(p) ? NickCases.Genitive(_players.Name(p)) : _players.Name(p)));

    // ------------------------------------------------------------------ створення

    public PadelReply Create(PadelWho who, PadelMatchRequest b)
    {
        if (who.Pid is null && !who.Admin) return PadelReply.No("Грати можуть лише акаунти — увійди на головній", 403);
        if (b.Teams is not { Length: 2 } teams || teams.Any(t => t is not { Length: 2 }))
            return PadelReply.No("Потрібні дві команди по двоє");
        var flat = teams.SelectMany(t => t).ToArray();
        if (flat.Any(p => !Pid.Valid(p) || !_players.Exists(p))) return PadelReply.No("Нема такого гравця");
        if (flat.Distinct(StringComparer.Ordinal).Count() != 4) return PadelReply.No("Потрібні четверо різних гравців");
        var rules = PadelRules.From(b.Rules);
        if (rules.Problem() is { } bad) return PadelReply.No(bad);
        if (b.First is not null && Array.IndexOf(PadelScore.Slots, b.First) < 0) return PadelReply.No("Невідомий подавач");
        var gathering = string.IsNullOrWhiteSpace(b.Gathering) ? null : b.Gathering.Trim();
        var agenda = gathering is null ? null : _agenda.Find(gathering);
        if (gathering is not null && agenda is null) return PadelReply.No("Нема такого збору");
        if (b.CourtUntil is { } cu && UntilProblem(cu) is { } badUntil) return PadelReply.No(badUntil);
        var until = b.CourtUntil ?? agenda?.Until;
        var (_, view) = Open(who, [.. teams.Select(t => t.ToArray())], rules, b.First, gathering, until, null);
        return new(new { ok = true, match = view });
    }

    /// <summary>Живий матч турніру (mode points, total турніру або null для «на час»).</summary>
    public (string Key, object View) OpenForTour(PadelWho who, string[][] teams, int? total, PadelTourRef tour) =>
        Open(who, teams, new PadelRules("points", Total: total), null, null, null, tour);

    (string Key, object View) Open(PadelWho who, string[][] teams, PadelRules rules, string? first, string? gathering, DateTimeOffset? until, PadelTourRef? tour)
    {
        object view;
        PadelMatchRec m;
        lock (_lock)
        {
            var now = _clock.UtcNow;
            m = new PadelMatchRec
            {
                Id = _next++, Rules = rules, Teams = teams, First = first is null ? PadelScore.Slots : PadelScore.Fresh(first).Order,
                By = who.Name, ByPid = who.Pid ?? "", CreatedAt = now, LastAt = now, CourtUntil = until, Tour = tour,
                Gathering = gathering, Seq = 1,
            };
            m.Replay();
            m.Last = new(1, ["start"], $"Нова гра: {Team(m, 0)} проти {Team(m, 1)}", null, who.Name);
            _all[m.Id] = m;
            Save(m);
            view = View(m);
        }
        _wire.Match(view);
        _ping.Ping();
        var a = Team(m, 0);
        var bb = Team(m, 1);
        _voice.Want([$"Перевага: {a}", $"Перевага: {bb}", $"Сет! {a}", $"Сет! {bb}", $"Матч! Перемогли {a}", $"Матч! Перемогли {bb}"]);
        WantNext(m);
        return (m.Key, view);
    }

    // ------------------------------------------------------------------ дії

    bool CanControl(PadelMatchRec m, PadelWho who)
    {
        if (who.Admin) return true;
        if (who.Pid is null) return false;
        return Ctl(m).Contains(who.Pid);
    }

    List<string> Ctl(PadelMatchRec m)
    {
        var ctl = new List<string>();
        if (m.ByPid.Length > 0) ctl.Add(m.ByPid);
        foreach (var p in m.Teams.SelectMany(t => t))
        {
            var c = _players.Canon(p);
            if (Pid.IsUser(c)) ctl.Add(c);
        }
        if (m.Tour is { } t && Link?.Organizer(t.Id) is { } org) ctl.Add(org);
        return [.. ctl.Distinct(StringComparer.Ordinal)];
    }

    public PadelReply Act(string id, PadelWho who, PadelActRequest b)
    {
        object view;
        PadelMatchRec m;
        bool finished, reopened;
        lock (_lock)
        {
            if (ParseId(id) is not { } n || !_all.TryGetValue(n, out m!)) return PadelReply.No("Нема такого матчу", 404);
            if (who.Pid is null && !who.Admin) return PadelReply.No("Керувати можуть лише акаунти — увійди на головній", 403);
            if (!CanControl(m, who)) return PadelReply.No("Керують гравці цього матчу", 403);
            // Дія по застарілому виду: не кладемо, а віддаємо свіжий — клієнт тихо його покаже
            if (b.Seq is { } seen && seen != m.Seq && b.A is "point" or "undo" or "serve")
                return new(new { ok = false, message = Stale, match = View(m) }, Stale, 409);
            var wasDone = m.Status == "done";
            var s = m.R.State;
            string[] events;
            string text;
            string? say;
            switch (b.A)
            {
                case "point":
                    if (b.T is not (0 or 1)) return PadelReply.No("Кому очко — 0 чи 1?");
                    if (m.Status != "live" || s.Over) return PadelReply.No("Матч уже скінчено");
                    m.Journal.Add(b.T.Value.ToString(CultureInfo.InvariantCulture));
                    m.Replay();
                    events = [.. m.R.LastEvents.Select(e => e.K)];
                    text = Describe(m, m.R.LastEvents, b.T.Value);
                    say = PadelScore.Speak(m.R.State, m.Rules, m.R.LastEvents, t => Team(m, t));
                    break;
                case "undo":
                    if (m.Status == "abandoned") return PadelReply.No("Матч скасовано");
                    if (m.Journal.Count == 0) return PadelReply.No("Нема що скасовувати");
                    m.Journal.RemoveAt(m.Journal.Count - 1);
                    m.Replay();
                    events = ["undo"];
                    text = "↶ Скасовано";
                    say = PadelScore.Speak(m.R.State, m.Rules, [], t => Team(m, t));
                    break;
                case "serve":
                    if (m.Status != "live") return PadelReply.No("Матч уже скінчено");
                    if (b.Slot is null || Array.IndexOf(PadelScore.Slots, b.Slot) < 0) return PadelReply.No("Невідомий гравець");
                    if (PadelScore.SetServer(s.Clone(), m.Rules, b.Slot) is { } why) return PadelReply.No(why);
                    if (s.ServerSlot != b.Slot) m.Journal.Add("s:" + b.Slot);
                    m.Replay();
                    events = ["server"];
                    text = say = $"Подає {_players.Name(m.SlotPid(b.Slot))}";
                    break;
                case "finish":
                    if (m.Status != "live") return PadelReply.No("Матч уже скінчено");
                    if (!s.Over) m.Journal.Add("f");
                    m.Replay();
                    events = ["match"];
                    text = m.R.State.Winner < 0 ? "🏁 Завершено — нічия" : $"🏁 Завершено — перемогли {Team(m, m.R.State.Winner)}";
                    say = PadelScore.Speak(m.R.State, m.Rules, [new("match", m.R.State.Winner)], t => Team(m, t));
                    break;
                case "abandon":
                    if (m.Status != "live") return PadelReply.No("Матч уже скінчено");
                    m.Status = "abandoned";
                    m.EndedAt = _clock.UtcNow;
                    m.EndedBy = who.Name;
                    events = ["abandon"];
                    text = "Матч скасовано — без результату";
                    say = null;
                    break;
                case "until":
                    if (m.Status != "live") return PadelReply.No("Матч уже скінчено");
                    if (b.Until is { } bu && UntilProblem(bu) is { } why2) return PadelReply.No(why2);
                    m.CourtUntil = b.Until;
                    var left = b.Until - _clock.UtcNow;
                    m.Clock10 = left is { } l1 && l1 <= TimeSpan.FromMinutes(_options.CurrentValue.LastGameMinutes);
                    m.Clock0 = left is { } l0 && l0 <= TimeSpan.Zero;
                    events = ["until"];
                    text = b.Until is { } u ? $"⏳ Оренда до {TimeZoneInfo.ConvertTime(u, Days.Kyiv):HH:mm}" : "Годинник оренди вимкнено";
                    say = null;
                    break;
                default:
                    return PadelReply.No("Невідома дія");
            }
            finished = Settle(m, who.Name);
            reopened = wasDone && m.Status == "live";
            Touch(m, events, text, say, who.Name);
            view = View(m);
        }
        After(m, view, finished);
        if (reopened && m.Tour is not null) Link?.Reopened(m);
        return new(new { ok = true, match = view });
    }

    public const string Stale = "Рахунок уже змінився";

    /// <summary>Годинник оренди: не в минулому (5 хв запасу на «щойно скінчилась») і не далі ніж за 12 годин.</summary>
    string? UntilProblem(DateTimeOffset until)
    {
        var now = _clock.UtcNow;
        if (until < now - TimeSpan.FromMinutes(5)) return "Оренда вже скінчилась — глянь час";
        if (until > now + TimeSpan.FromHours(12)) return "Оренда — не далі ніж на 12 годин наперед";
        return null;
    }

    /// <summary>Статус за станом: скінчився — done (true, якщо щойно), «скасувати» після кінця — знову live.</summary>
    bool Settle(PadelMatchRec m, string by)
    {
        var s = m.R.State;
        if (m.StartedAt is null && s.Log.Count > 0) m.StartedAt = _clock.UtcNow;
        if (m.Status == "live" && s.Over)
        {
            m.Status = "done";
            m.EndedAt = _clock.UtcNow;
            m.EndedBy = by;
            return true;
        }
        if (m.Status == "done" && !s.Over) { m.Status = "live"; m.EndedAt = null; m.EndedBy = null; }
        return false;
    }

    void Touch(PadelMatchRec m, string[] events, string text, string? say, string by)
    {
        m.Seq++;
        m.LastAt = _clock.UtcNow;
        m.Last = new(m.Seq, events, text, say, by);
        Save(m);
    }

    /// <summary>Після замка: розіслати, наперед озвучити, а щойно завершений — у турнір або в балачки.</summary>
    void After(PadelMatchRec m, object view, bool finished)
    {
        _wire.Match(view);
        _ping.Ping();
        if (m.Last?.Say is { } say) _voice.Want([say], urgent: true);
        WaitClip(m);
        WantNext(m);
        if (m.Status == "abandoned" && m.Tour is not null) Link?.Dropped(m);
        if (!finished) return;
        if (m.Tour is not null) Link?.Scored(m, m.EndedBy ?? m.By);
        else Chat(m);
        _wire.Rating();
    }

    /// <summary>Як часто дивитись, чи з'явився кліп останньої фрази (тести ставлять менше).</summary>
    public TimeSpan ClipPoll { get; set; } = TimeSpan.FromMilliseconds(400);
    const int ClipTries = 20;   // ≈8 с — далі фраза вже не до речі

    /// <summary>
    /// Кліп останньої фрази ще не готовий (edge-tts інколи думає довше за тиць): чекаємо до ≈8 с і розсилаємо той самий
    /// seq уже з кліпом — табло його програє. Нове очко за цей час — чекання саме зникає.
    /// </summary>
    void WaitClip(PadelMatchRec m)
    {
        if (!_voice.On || m.Last is not { Say: { } say } last || _voice.Clip(say) is not null) return;
        var seq = last.Seq;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < ClipTries; i++)
            {
                await Task.Delay(ClipPoll);
                object view;
                lock (_lock)
                {
                    if (m.Seq != seq || m.Last?.Say != say) return;
                    if (_voice.Clip(say) is null) continue;
                    view = View(m);
                }
                _wire.Match(view);
                return;
            }
        });
    }

    /// <summary>Фрази обох можливих наступних очок — щоб Глек не запізнювався.</summary>
    void WantNext(PadelMatchRec m)
    {
        if (!_voice.On || m.Status != "live") return;
        var texts = new List<string>();
        for (var t = 0; t < 2; t++)
        {
            var c = m.R.State.Clone();
            var ev = PadelScore.Step(c, t, m.Rules);
            texts.Add(PadelScore.Speak(c, m.Rules, ev, x => Team(m, x)));
        }
        _voice.Want(texts);
    }

    string Describe(PadelMatchRec m, List<PadelEvent> ev, int t)
    {
        var parts = new List<string>();
        foreach (var e in ev)
        {
            var part = e.K switch
            {
                "match" => e.T < 0 ? "🏁 Нічия" : $"🏆 Матч — {Team(m, e.T)}",
                "set" => $"Сет — {Team(m, e.T)}",
                "game" => (e.Brk ? "Брейк! " : "") + $"Гейм — {Team(m, e.T)}",
                "tb" => "Тайбрейк",
                "stb" => "Супертайбрейк",
                "deuce" => e.Decisive ? (m.Rules.Deuce == "star" ? "Рівно · ⭐ star point" : "Рівно · золоте очко") : "Рівно",
                "ends" => "↔ Зміна сторін",
                "serve" => "Перехід подачі",
                _ => null,
            };
            if (part is not null) parts.Add(part);
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : $"Очко — {Team(m, t)}";
    }

    void Chat(PadelMatchRec m)
    {
        lock (_lock)
        {
            if (m.Chatted || !m.Counts) return;
            m.Chatted = true;
            Save(m);
        }
        var s = m.R.State;
        var score = m.Rules.Points ? $"{s.Pts[0]}:{s.Pts[1]}" : string.Join(" · ", s.Sets.Select(x => $"{x.G[0]}:{x.G[1]}"));
        var text = s.Winner >= 0
            ? $"🍳 Падельня: {Team(m, s.Winner)} перемогли {TeamAcc(m, 1 - s.Winner)} — {score}"
            : $"🍳 Падельня: {Team(m, 0)} та {Team(m, 1)} зіграли внічию — {score}";
        var line = _db.AddChat(_site.CurrentValue.DjName is { Length: > 0 } dj ? dj : "Дядько Глек", text, "padel");   // рядок від Глека, як інші його рядки в балачках
        _wire.Chat(new
        {
            id = line.Id, nick = line.Nick, text = line.Text, at = line.At, kind = line.Kind, roomId = line.RoomId,
            replyTo = line.ReplyTo, replyNick = line.ReplyNick, replyText = line.ReplyText, likes = line.Likes, topic = line.Topic,
        });
    }

    // ------------------------------------------------------------------ фоновий таймер

    /// <summary>Раз на 15 с: годинник оренди (10 хв і 0) і закриття матчів, яких 6 годин ніхто не чіпав.</summary>
    public void Tick()
    {
        var sends = new List<(PadelMatchRec M, object View, bool Finished)>();
        lock (_lock)
        {
            var now = _clock.UtcNow;
            var lastGame = TimeSpan.FromMinutes(_options.CurrentValue.LastGameMinutes);
            foreach (var m in _all.Values.Where(x => x.Status == "live"))
            {
                if (now - m.LastAt >= Idle)
                {
                    if (m.Counts)
                    {
                        if (!m.R.State.Over) m.Journal.Add("f");
                        m.Replay();
                    }
                    else { m.Status = "abandoned"; m.EndedAt = now; }
                    var fin = Settle(m, "Глек");
                    Touch(m, [m.Status == "done" ? "match" : "abandon"], "Матч закрито — 6 годин без дій", null, "Глек");
                    sends.Add((m, View(m), fin));
                    continue;
                }
                if (m.CourtUntil is not { } until) continue;
                if (!m.Clock10 && now >= until - lastGame)
                {
                    m.Clock10 = true;
                    if (now < until)
                    {
                        var min = _options.CurrentValue.LastGameMinutes;
                        Touch(m, ["clock10"], $"⏳ {min} хвилин оренди — останній гейм", $"{min} хвилин оренди — останній гейм", "Глек");
                        sends.Add((m, View(m), false));
                    }
                }
                if (!m.Clock0 && now >= until)
                {
                    m.Clock0 = true;
                    Touch(m, ["clock0"], "⏰ Оренда скінчилась", "Оренда скінчилась", "Глек");
                    sends.Add((m, View(m), false));
                }
            }
        }
        foreach (var (m, view, fin) in sends) After(m, view, fin);
    }

    /// <summary>На старті сервера: усі «x — y» і службові фрази.</summary>
    public void Warm()
    {
        var min = _options.CurrentValue.LastGameMinutes;
        _voice.Want([.. PadelScore.CommonPhrases(), $"{min} хвилин оренди — останній гейм", "Оренда скінчилась"]);
    }

    // ------------------------------------------------------------------ вид

    public object? View(string id)
    {
        lock (_lock) return ParseId(id) is { } n && _all.TryGetValue(n, out var m) ? View(m) : null;
    }

    public object? Stats(string id)
    {
        lock (_lock)
        {
            if (ParseId(id) is not { } n || !_all.TryGetValue(n, out var m)) return null;
            var st = m.R.Stats;
            return new
            {
                serve = st.Serve.Select(x => new { won = x.Won, of = x.Of }), recv = st.Recv.Select(x => new { won = x.Won, of = x.Of }),
                streak = st.Streak, breaks = st.Breaks, golden = st.Golden, momentum = st.Momentum,
            };
        }
    }

    public IReadOnlyList<object> Live()
    {
        lock (_lock) return [.. _all.Values.Where(m => m.Status == "live").OrderByDescending(m => m.CreatedAt).Select(View)];
    }

    public IReadOnlyList<object> Recent(int n)
    {
        lock (_lock)
            return [.. _all.Values.Where(m => m.Status == "done").OrderByDescending(m => m.EndedAt).Take(Math.Clamp(n, 1, 200)).Select(View)];
    }

    /// <summary>Знімок для історії, лобі й списку гравців (читати, не міняти).</summary>
    public IReadOnlyList<PadelMatchRec> All()
    {
        lock (_lock) return [.. _all.Values];
    }

    /// <summary>Скасувати живий матч турніру (турнір видалено).</summary>
    public void Drop(string matchKey)
    {
        object? view = null;
        lock (_lock)
        {
            if (ParseId(matchKey) is not { } n || !_all.TryGetValue(n, out var m) || m.Status != "live") return;
            m.Status = "abandoned";
            m.EndedAt = _clock.UtcNow;
            Touch(m, ["abandon"], "Турнір скасовано — матч теж", null, "Глек");
            view = View(m);
        }
        _wire.Match(view);
        _ping.Ping();
    }

    object View(PadelMatchRec m)
    {
        var s = m.R.State;
        var r = m.Rules;
        var hint = PadelScore.Hint(s, r);
        var live = m.Status == "live" && !s.Over;
        var slot = s.ServerSlot;
        return new
        {
            id = m.Key,
            status = m.Status,
            rules = new { mode = r.Mode, deuce = r.Deuce, sets = r.Sets, setTo = r.SetTo, total = r.Total },
            teams = m.Teams.Select(t => t.Select(_players.P).ToArray()).ToArray(),
            state = new
            {
                pts = s.Pts, games = s.Games, sets = s.Sets.Select(x => new { g = x.G, tb = x.Tb, stb = x.Stb }).ToArray(),
                won = s.Won, tb = s.Tb, superTb = s.SuperTb, deuces = s.Deuces, order = s.Order, srv = s.Srv, over = s.Over,
                winner = s.Winner,
            },
            label = new[] { PadelScore.Label(s, r, 0), PadelScore.Label(s, r, 1) },
            server = live ? new { slot, pid = m.SlotPid(slot), side = PadelScore.Side(s, r) } : null,
            hint = live && hint is { } h ? new { text = h.Text, team = h.Team } : null,
            score = PadelScore.ScoreText(s, r),
            log = s.Log,
            startedAt = m.StartedAt,
            endedAt = m.EndedAt,
            courtUntil = m.CourtUntil,
            tour = m.Tour is { } t ? new { id = t.Id, round = t.Round, court = t.Court } : null,
            gathering = m.Gathering,
            by = m.By,
            ctl = Ctl(m),
            last = m.Last is { } l
                ? new { seq = l.Seq, events = l.Events, text = l.Text, say = l.Say is null ? null : new { text = l.Say, clip = _voice.Clip(l.Say) }, by = l.By }
                : null,
        };
    }
}
