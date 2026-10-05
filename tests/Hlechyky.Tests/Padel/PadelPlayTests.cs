using System.Text.Json;
using Hlechyky.Padel;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Hlechyky.Tests.Padel;

/// <summary>
/// Половина «Гра» Падельні (контракт §2.1, §2.3, §2.5): гості й прив'язка, живі матчі (права, журнал, «скасувати»,
/// подача, завершення, рестарт, годинник оренди, авто-закриття, голос, балачки), лобі. База — тимчасова, розсилка й
/// голос — записувачі.
/// </summary>
public sealed class PadelPlayTests : IDisposable
{
    public sealed class Wire : IPadelWire
    {
        readonly object _l = new();
        public List<JsonElement> Matches { get; } = [];
        public List<JsonElement> Tours { get; } = [];
        public List<JsonElement> Lobbies { get; } = [];
        public List<JsonElement> Chats { get; } = [];
        public int Ratings;
        public void Match(object view) { lock (_l) Matches.Add(Views.Json(view)); }
        public void Tournament(object view) { lock (_l) Tours.Add(Views.Json(view)); }
        public void Gathering(object view) { }
        public void Money() { }
        public void Rating() => Interlocked.Increment(ref Ratings);
        public void Lobby(object summary) { lock (_l) Lobbies.Add(Views.Json(summary)); }
        public List<(string Nick, string Text)> Toasts { get; } = [];
        public void Toast(string nick, string text) { lock (_l) Toasts.Add((nick, text)); }
        public List<JsonElement> MatchesNow() { lock (_l) return [.. Matches]; }
        public void Chat(object line) { lock (_l) Chats.Add(Views.Json(line)); }
    }

    public sealed class Voice : IPadelVoice
    {
        public bool On => true;
        public HashSet<string> Ready { get; } = [];
        public List<string> Wanted { get; } = [];
        public string? Clip(string text)
        {
            lock (Ready)
            {
                if (Ready.Contains(text)) return $"/api/padel/voice/{PadelScore.VoiceId(text)}.mp3";
                Wanted.Add(text);
                return null;
            }
        }
        public void Want(IEnumerable<string> texts, bool urgent = false) => Wanted.AddRange(texts);
        public string? File(string id) => null;
    }

    /// <summary>Усе Падельні гри на одній тимчасовій базі; <see cref="Reopen"/> — «рестарт сервера».</summary>
    public sealed class Rig : IDisposable
    {
        public TempDb Tmp { get; } = new();
        public FakeClock Clock { get; } = new();
        public Wire W { get; } = new();
        public Voice V { get; } = new();
        public PadelPlayers Players { get; private set; } = null!;
        public PadelMatches Matches { get; private set; } = null!;
        public PadelTours Tours { get; private set; } = null!;
        public FixedOptions<PadelOptions> Options { get; } = new(new PadelOptions());

        public static readonly string[] Nicks = ["Влад", "Микола", "Smaug", "Андрій", "Оля", "Таня", "Діма", "Костя", "Ірина", "Сашко", "Петро", "Іра"];

        public Rig()
        {
            foreach (var n in Nicks) Tmp.Db.AddAccount(n, "h", "s");
            Reopen();
        }

        public void Reopen()
        {
            Players = new PadelPlayers(Tmp.Db, Clock);
            var ping = new PadelPing(W, Clock);
            Matches = new PadelMatches(Tmp.Db, Players, W, V, new NoPadelAgenda(), Clock, ping, Options, new FixedOptions<SiteOptions>(new SiteOptions()));
            Tours = new PadelTours(Tmp.Db, Players, Matches, W, new NoPadelAgenda(), Clock, ping, new FixedOptions<SiteOptions>(new SiteOptions()));
            ping.Summary = () => new PadelLobby(Matches, Tours, Players, new NoPadelAgenda()).Summary();
        }

        public static PadelWho Who(string nick, bool admin = false) => new(Pid.User(nick), nick, admin);
        public static string U(string nick) => Pid.User(nick);
        public void Dispose() => Tmp.Dispose();
    }

    readonly Rig _r = new();
    public void Dispose() => _r.Dispose();

    static JsonElement Body(PadelReply r)
    {
        Assert.True(r.Error is null, r.Error);
        return Views.Json(r.Body);
    }

    static string[][] Teams => [[Rig.U("Влад"), Rig.U("Микола")], [Rig.U("Smaug"), Rig.U("Андрій")]];

    JsonElement NewMatch(PadelRules? rules = null, DateTimeOffset? until = null) =>
        Body(_r.Matches.Create(Rig.Who("Влад"), new PadelMatchRequest(Teams, rules, null, null, until))).GetProperty("match");

    JsonElement Act(string id, string a, int? t = null, string? slot = null, string who = "Влад") =>
        Body(_r.Matches.Act(id, Rig.Who(who), new PadelActRequest(a, t, slot, null))).GetProperty("match");

    // ============================================================ гравці

    [Fact]
    public void Guests_dedupe_refuse_account_names_and_link_to_account()
    {
        var p = _r.Players;
        var g = Body(p.AddGuest("  Вася  ", "Влад")).GetProperty("player");
        Assert.Equal("Вася", g.GetProperty("name").GetString());
        var pid = g.GetProperty("pid").GetString()!;
        Assert.StartsWith("g:", pid);
        Assert.Equal(pid, Body(p.AddGuest("вася", "Оля")).GetProperty("player").GetProperty("pid").GetString());
        Assert.Equal("Це акаунт — обери його зі списку", p.AddGuest("влад", "Оля").Error);
        Assert.NotNull(p.AddGuest("В", "Оля").Error);
        Assert.Equal(pid, p.Canon(pid));
        Body(p.Link(pid, Rig.U("Костя")));
        Assert.Equal(Rig.U("Костя"), p.Canon(pid));
        Assert.Equal("Вася", p.Name(pid));
        Assert.Equal("Smaug", p.Name(Rig.U("smaug")));
        Assert.True(p.Exists(Rig.U("Таня")));
        Assert.False(p.Exists(Rig.U("Нема")));
        // Прив'язаний «Вася» вже не той, кого повертає новий запис з таким ім'ям
        Assert.NotEqual(pid, Body(p.AddGuest("Вася", "Оля")).GetProperty("player").GetProperty("pid").GetString());
    }

    [Fact]
    public void Players_list_has_played_people_guests_and_all_accounts()
    {
        var g = Body(_r.Players.AddGuest("Гість Петя", "Влад")).GetProperty("player").GetProperty("pid").GetString()!;
        var m = NewMatch(new PadelRules("points", Total: 16));
        for (var i = 0; i < 16; i++) Act(m.GetProperty("id").GetString()!, "point", i % 2);
        var res = (IValueHttpResult)PadelPlaySetup.Players(_r.Players, _r.Matches, _r.Tours);
        var j = Views.Json(res.Value);
        var players = j.GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(5, players.Count);
        Assert.Equal(1, players.First(x => x.GetProperty("pid").GetString() == Rig.U("Влад")).GetProperty("played").GetInt32());
        Assert.Contains(players, x => x.GetProperty("pid").GetString() == g && x.GetProperty("guest").GetBoolean());
        Assert.Equal(Rig.Nicks.Length, j.GetProperty("accounts").GetArrayLength());
    }

    // ============================================================ табло

    [Fact]
    public void Match_view_shape_rights_points_undo_serve_finish()
    {
        var m = NewMatch();
        var id = m.GetProperty("id").GetString()!;
        Assert.Equal("live", m.GetProperty("status").GetString());
        Assert.Equal("adv", m.GetProperty("rules").GetProperty("deuce").GetString());
        Assert.Equal("Влад", m.GetProperty("teams")[0][0].GetProperty("name").GetString());
        Assert.Equal("A0", m.GetProperty("server").GetProperty("slot").GetString());
        Assert.Equal("right", m.GetProperty("server").GetProperty("side").GetString());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("tour").ValueKind);
        Assert.Contains(Rig.U("Smaug"), m.GetProperty("ctl").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal(403, _r.Matches.Act(id, Rig.Who("Оля"), new("point", 0, null, null)).Status);
        Assert.Equal("Керують гравці цього матчу", _r.Matches.Act(id, Rig.Who("Оля"), new("point", 0, null, null)).Error);
        Assert.Equal(403, _r.Matches.Act(id, new PadelWho(null, "гість", false), new("point", 0, null, null)).Status);
        Assert.Equal(200, _r.Matches.Act(id, Rig.Who("Оля", admin: true), new("point", 0, null, null)).Status);

        m = Act(id, "point", 0, who: "Smaug");
        Assert.Equal("30", m.GetProperty("label")[0].GetString());
        Assert.Equal("тридцять — нуль", m.GetProperty("last").GetProperty("say").GetProperty("text").GetString());
        Assert.Equal("Smaug", m.GetProperty("last").GetProperty("by").GetString());
        Assert.Equal("Подавача можна поміняти лише на початку гейму", _r.Matches.Act(id, Rig.Who("Влад"), new("serve", null, "B1", null)).Error);
        Act(id, "point", 0);
        m = Act(id, "point", 0);
        Assert.Equal(["game", "ends"], m.GetProperty("last").GetProperty("events").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Гейм — Влад і Микола · ↔ Зміна сторін", m.GetProperty("last").GetProperty("text").GetString());
        Assert.Equal("1:0 · 0:0", m.GetProperty("score").GetString());
        m = Act(id, "undo");
        Assert.Equal("40", m.GetProperty("label")[0].GetString());
        Act(id, "point", 0);
        m = Act(id, "serve", slot: "B1");
        Assert.Equal("B1", m.GetProperty("server").GetProperty("slot").GetString());
        Assert.Equal("Подає Андрій", m.GetProperty("last").GetProperty("text").GetString());
        m = Act(id, "finish");
        Assert.Equal("done", m.GetProperty("status").GetString());
        Assert.Equal(0, m.GetProperty("state").GetProperty("winner").GetInt32());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("server").ValueKind);
        Assert.Equal("Матч уже скінчено", _r.Matches.Act(id, Rig.Who("Влад"), new("point", 0, null, null)).Error);
        // Замало для балачок: один гейм є — рядок буде
        var chat = Assert.Single(_r.W.Chats);
        Assert.Equal("padel", chat.GetProperty("kind").GetString());
        Assert.StartsWith("🍳 Падельня: Влад і Микола перемогли Smaug і Андрія — 1:0", chat.GetProperty("text").GetString());
        // «Скасувати» після кінця — знову live; повторне завершення не спамить балачки
        m = Act(id, "undo");
        Assert.Equal("live", m.GetProperty("status").GetString());
        Act(id, "finish");
        Assert.Single(_r.W.Chats);
        Assert.True(_r.W.Ratings >= 2);
        Assert.NotEmpty(_r.W.Lobbies);
        var stats = Views.Json(_r.Matches.Stats(id));
        Assert.Equal(4, stats.GetProperty("momentum").GetArrayLength());
        Assert.Equal(4, stats.GetProperty("serve")[0].GetProperty("of").GetInt32());
    }

    [Fact]
    public void Create_validates_players_and_rules()
    {
        Assert.Equal("Потрібні четверо різних гравців",
            _r.Matches.Create(Rig.Who("Влад"), new([[Rig.U("Влад"), Rig.U("Влад")], [Rig.U("Smaug"), Rig.U("Андрій")]], null, null, null, null)).Error);
        Assert.Equal("Нема такого гравця",
            _r.Matches.Create(Rig.Who("Влад"), new([[Rig.U("Влад"), "g:99"], [Rig.U("Smaug"), Rig.U("Андрій")]], null, null, null, null)).Error);
        Assert.Equal(403, _r.Matches.Create(new PadelWho(null, "гість", false), new(Teams, null, null, null, null)).Status);
        Assert.Null(_r.Matches.Create(new PadelWho(null, "гість", true), new(Teams, null, null, null, null)).Error);
        Assert.NotNull(_r.Matches.Create(Rig.Who("Влад"), new(Teams, new PadelRules(Deuce: "silver"), null, null, null)).Error);
        var m = Body(_r.Matches.Create(Rig.Who("Влад"), new(Teams, null, "B0", null, null))).GetProperty("match");
        Assert.Equal("B0", m.GetProperty("server").GetProperty("slot").GetString());
    }

    [Fact]
    public void Match_survives_restart()
    {
        var id = NewMatch(new PadelRules(Sets: "3", Deuce: "golden")).GetProperty("id").GetString()!;
        foreach (var t in new[] { 0, 1, 0, 1, 0, 1 }) Act(id, "point", t);
        _r.Reopen();
        var m = Views.Json(_r.Matches.View(id));
        Assert.Equal("40:40", m.GetProperty("score").GetString()[^5..]);
        Assert.Equal(("Золоте очко", -1), (m.GetProperty("hint").GetProperty("text").GetString(), m.GetProperty("hint").GetProperty("team").GetInt32()));
        Assert.Equal("choice", m.GetProperty("server").GetProperty("side").GetString());
        Assert.Equal(7, m.GetProperty("last").GetProperty("seq").GetInt32());
        m = Act(id, "point", 1);
        Assert.Equal("0:1 · 0:0", m.GetProperty("score").GetString());
    }

    [Fact]
    public void Idle_matches_close_after_six_hours()
    {
        var a = NewMatch().GetProperty("id").GetString()!;
        for (var i = 0; i < 8; i++) Act(a, "point", 0);
        var b = NewMatch().GetProperty("id").GetString()!;
        Act(b, "point", 1);
        _r.Clock.Advance(TimeSpan.FromHours(5));
        _r.Matches.Tick();
        Assert.Equal("live", Views.Json(_r.Matches.View(a)).GetProperty("status").GetString());
        _r.Clock.Advance(TimeSpan.FromHours(1.01));
        _r.Matches.Tick();
        Assert.Equal("done", Views.Json(_r.Matches.View(a)).GetProperty("status").GetString());
        Assert.Equal("abandoned", Views.Json(_r.Matches.View(b)).GetProperty("status").GetString());
        Assert.Single(_r.W.Chats);
    }

    [Fact]
    public void Court_clock_warns_once_at_ten_and_zero()
    {
        var id = NewMatch(until: _r.Clock.UtcNow.AddMinutes(12)).GetProperty("id").GetString()!;
        var n = _r.W.Matches.Count;
        _r.Matches.Tick();
        Assert.Equal(n, _r.W.Matches.Count);
        _r.Clock.Advance(TimeSpan.FromMinutes(3));
        _r.Matches.Tick();
        _r.Matches.Tick();
        var last = _r.W.Matches[^1].GetProperty("last");
        Assert.Equal("clock10", last.GetProperty("events")[0].GetString());
        Assert.Equal("⏳ 10 хвилин оренди — останній гейм", last.GetProperty("text").GetString());
        Assert.Equal(n + 1, _r.W.Matches.Count);
        _r.Clock.Advance(TimeSpan.FromMinutes(10));
        _r.Matches.Tick();
        Assert.Equal("clock0", _r.W.Matches[^1].GetProperty("last").GetProperty("events")[0].GetString());
        Assert.Equal(n + 2, _r.W.Matches.Count);
        // Продовжили оренду — годинник знову попередить
        Act(id, "until", who: "Микола");
        Assert.Equal(JsonValueKind.Null, Views.Json(_r.Matches.View(id)).GetProperty("courtUntil").ValueKind);
    }

    [Fact]
    public void Voice_clip_when_ready_and_next_points_prepared()
    {
        var id = NewMatch().GetProperty("id").GetString()!;
        Assert.Contains("Перевага: Влад і Микола", _r.V.Wanted);
        Assert.Contains("п’ятнадцять — нуль", _r.V.Wanted);
        Assert.Contains("нуль — п’ятнадцять", _r.V.Wanted);
        var m = Act(id, "point", 1);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("last").GetProperty("say").GetProperty("clip").ValueKind);
        _r.V.Ready.Add("нуль — п’ятнадцять");
        var clip = Views.Json(_r.Matches.View(id)).GetProperty("last").GetProperty("say").GetProperty("clip").GetString();
        Assert.Equal($"/api/padel/voice/{PadelScore.VoiceId("нуль — п’ятнадцять")}.mp3", clip);
        Assert.Contains("нуль — тридцять", _r.V.Wanted);
        Assert.Equal(404, ((IStatusCodeHttpResult)PadelPlaySetup.Voice("0123456789abcdef.mp3", _r.V)).StatusCode);
    }

    [Fact]
    public void Lobby_lists_live_matches_and_tours()
    {
        var id = NewMatch().GetProperty("id").GetString()!;
        Act(id, "point", 0);
        var l = Views.Json(new PadelLobby(_r.Matches, _r.Tours, _r.Players, new NoPadelAgenda()).Summary());
        var live = Assert.Single(l.GetProperty("live").EnumerateArray());
        Assert.Equal("0:0 · 15:0", live.GetProperty("score").GetString());
        Assert.Equal("Smaug", live.GetProperty("teams")[1][0].GetString());
        Assert.Equal(JsonValueKind.Null, l.GetProperty("next").ValueKind);
    }
}
