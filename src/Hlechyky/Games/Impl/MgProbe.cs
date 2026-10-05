using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Проба міні-гри (<c>mgprobe</c>) — стенд для агентів хвилі «Вечірка»: одна міні-гра в режимі вечірки (люди за столом
/// плюс боти) через той самий <see cref="MinigameHost"/>, що візьме «Глечикова вечірка». Господар тисне «Почати» →
/// картка «як грати» 3 с → міні-гра → таблиця місць → «Ще раз» (можна іншу гру). У лобі гри нема
/// (<see cref="IUnlistedGame"/>): стіл відкривають посиланням <c>/#games/new/mgprobe</c>.
/// <para>Партія ніколи не кличе Finish: «Ще раз» — своя дія, а не рематч кімнати. Так стенд не платить черепків і не
/// пише результатів у таблиці.</para>
/// </summary>
public sealed class MgProbe : Game, IUnlistedGame
{
    public const int TickMillis = 20;
    /// <summary>Скільки висить картка «як грати» перед стартом.</summary>
    public const int HowtoMs = 3000;
    public const int MaxSeats = 8;

    static readonly GameOption GameOpt = new("game", "Міні-гра",
        [.. PartyPool.Games.Select(g => (g.Id, g.Title))], PartyPool.Games.FirstOrDefault()?.Id ?? "icefloe");

    static readonly GameOption BotsOpt = new("bots", "Ботів",
        [.. Enumerable.Range(0, MaxSeats).Select(n => (n.ToString(), n == 0 ? "без ботів" : $"{n} 🤖"))], "2");

    /// <summary>Той самий ключ і значення, що <see cref="LiveBots.LevelOption"/>, але підпис — про ботів стенда.</summary>
    static readonly GameOption LevelOpt = new(LiveBots.LevelOption.Key, "🤖 Рівень ботів", LiveBots.LevelOption.Values, LiveBots.LevelOption.Default);

    public override GameInfo Info { get; } = new(
        "mgprobe", "Проба міні-гри", "пробу міні-гри", GameGroup.Party, 1, MaxSeats, TickMs: TickMillis,
        Start: StartMode.ByHost, Options: [GameOpt, BotsOpt, LevelOpt],
        Hint: "Стенд розробника: одна міні-гра вечірки з ботами — перевірити, як вона живе всередині вечірки");

    string _gameId = "icefloe";
    int _botsWanted = 2;
    LiveBots.Level _level = LiveBots.Level.Normal;

    /// <summary>Фази: howto — картка правил, play — міні-гра, result — таблиця місць.</summary>
    string _phase = "howto";
    DateTimeOffset _howtoUntil;
    MinigameHost? _host;
    int _round;
    bool _dirty;

    public MinigameHost? Host => _host;
    public string Phase => _phase;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("game", out var g) && !string.IsNullOrEmpty(g))
        {
            if (!PartyPool.Has(g)) throw new GameError($"Міні-гри «{g}» нема в пулі вечірки");
            _gameId = g;
        }
        else if (PartyPool.Games.Count > 0 && !PartyPool.Has(_gameId)) _gameId = PartyPool.Games[0].Id;
        _botsWanted = options.TryGetValue("bots", out var b) && int.TryParse(b, out var n) ? Math.Clamp(n, 0, MaxSeats) : 2;
        _level = LiveBots.Read(options);
    }

    public override void Start()
    {
        _round = 0;
        Begin(_gameId);
    }

    /// <summary>Нова міні-гра: люди, що сидять зараз (за порядком місць), плюс боти — у межах PartyMin..PartyMax гри.</summary>
    void Begin(string id)
    {
        _gameId = id;
        _round++;
        var humans = Enumerable.Range(0, MaxSeats).Where(Ctx.Seated).ToList();
        var probe = PartyPool.Create(id) as IPartyMinigame;
        int min = probe?.PartyMin ?? 2, max = probe?.PartyMax ?? MaxSeats;
        var people = humans.Take(max).ToList();
        var bots = Math.Clamp(_botsWanted, Math.Max(0, min - people.Count), Math.Max(0, max - people.Count));
        var seats = people.Select(PartySeat.Human).Concat(Enumerable.Range(0, bots).Select(_ => PartySeat.BotAt())).ToList();
        _host = MinigameHost.Create(id, Ctx, seats, _level);
        _phase = "howto";
        _howtoUntil = Ctx.Clock.UtcNow.AddMilliseconds(HowtoMs);
        _dirty = true;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "mg":
                if (_phase != "play" || _host is null) return ActResult.Fail("Міні-гра ще не почалась");
                if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("a", out var a) || a.ValueKind != JsonValueKind.String)
                    return ActResult.Fail("Тут так не ходять");
                var p = payload.TryGetProperty("p", out var pp) ? pp : default;
                return _host.Act(seat, a.GetString()!, p);
            case "again":
                if (_phase != "result") return ActResult.Fail("Спершу дограйте");
                if (Ctx.HostSeat != seat) return ActResult.Fail("«Ще раз» тисне господар столу");
                var id = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.String
                    ? g.GetString()! : _gameId;
                if (!PartyPool.Has(id)) return ActResult.Fail("Такої міні-гри нема");
                Begin(id);
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    public override TickResult Tick()
    {
        var dirty = _dirty;
        _dirty = false;
        switch (_phase)
        {
            case "howto":
                if (Ctx.Clock.UtcNow < _howtoUntil) return dirty ? TickResult.Both : TickResult.None;
                if (_host is null) { _phase = "result"; return TickResult.Both; }
                _host.Start();
                _phase = _host.Over ? "result" : "play";
                return TickResult.Both;
            case "play":
                var r = _host!.Tick();
                if (_host.Over)
                {
                    _phase = "result";
                    var res = _host.Result!;
                    var names = _host.Names();
                    Ctx.Say($"🎯 {_host.Title}: " + string.Join(" · ",
                        Enumerable.Range(0, names.Length).OrderBy(i => res.Places[i]).Select(i => $"{res.Places[i]}. {names[i]}")));
                    return TickResult.Both;
                }
                return dirty ? TickResult.Both : r;
            default:
                return dirty ? TickResult.Both : TickResult.None;
        }
    }

    /// <summary>Стіл не кінчається, коли хтось устав: міні-гра доживе сама, а «Ще раз» збере тих, хто лишився.</summary>
    public override void OnLeave(int seat) { }

    public override object View(int? seat)
    {
        var h = _host;
        var res = h?.Result;
        return new
        {
            phase = _phase,
            round = _round,
            game = _gameId,
            title = h?.Title ?? PartyPool.Games.FirstOrDefault(g => g.Id == _gameId)?.Title ?? _gameId,
            howto = h?.Howto ?? "",
            until = _phase == "howto" ? _howtoUntil : (DateTimeOffset?)null,
            howtoMs = HowtoMs,
            n = h?.Seats ?? 0,
            names = h?.Names() ?? [],
            seatNames = h?.SeatNames() ?? [],
            nicks = h is null ? [] : Enumerable.Range(0, h.Seats).Select(h.Ctx.NickOf).ToArray(),
            sub = seat is { } s && h is not null ? h.SubOf(s) : null,
            mg = _phase == "howto" ? null : h?.View(seat),
            result = res is null ? null : new
            {
                places = res.Places,
                scores = Enumerable.Range(0, res.Places.Length).Select(i => res.Scores[i]).ToArray(),
                winners = res.Winners,
                how = res.How.ToString().ToLowerInvariant(),
                verdict = res.Verdict,
            },
            games = PartyPool.Games.Select(g => new[] { g.Id, g.Title }).ToArray(),
            bots = _botsWanted,
            turn = (int?)null,
        };
    }

    /// <summary>Кадр — лише кадр міні-гри; поза грою — порожній (вид прийде окремо).</summary>
    public override object? Frame() => new { mg = _phase == "play" ? _host?.Frame() : null };
}
