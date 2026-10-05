using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ярмарковий тир: вискакують глеки, пливуть качки, блимає золотий глек — клацай. У кожного своя копія тиру з однаковими
/// для всіх мішенями (розклад стенду з сервера), тож пінг не вирішує: клієнт шле постріл <c>{s, t, x, y}</c> з часом
/// відносно старту стенду, а сервер за розкладом перевіряє, чи мішень була там у ту мить. Спільне для столу
/// (<see cref="Tyr"/>) і «Тиру дня» (<see cref="TyrDaily"/>) — тут; правила копії — <see cref="TyrCore"/>.
/// Spec: <c>docs/games/specs/tyr.md</c>.
/// </summary>
public abstract class TyrBase : Game
{
    public const int Seats = 8;
    /// <summary>Скільки після кінця стенду ще ловимо постріли, що летіли до кінця (мс).</summary>
    public const int GraceMs = 400;
    /// <summary>Межі часу пострілу відносно годинника сервера: запізнення (пінг, джитер) і забігання наперед, мс.</summary>
    public const int MaxLag = 800, Ahead = 150;
    /// <summary>Мішені в кадрі: ті, що вискочать у найближчі 2 с, і ті, що зникли не раніше ніж 0,5 с тому.</summary>
    public const int LookMs = 2_000, TailMs = 500;
    /// <summary>Фази в кадрі (<c>ph</c>).</summary>
    public const int PhReady = 0, PhGo = 1, PhOver = 2, PhLobby = 3;

    /// <summary>Які стенди грає партія (<see cref="TyrCore.Shelf"/>…).</summary>
    protected int[] _stands = [TyrCore.Shelf, TyrCore.Water, TyrCore.Mix];
    protected List<TyrTarget>[] _targets = [];
    protected readonly TyrShooter[] _sh = [.. Enumerable.Range(0, Seats).Select(_ => new TyrShooter())];
    protected bool _started, _over;
    protected int _si;
    protected DateTimeOffset _standAt;
    protected int[] _winners = [];
    bool _dirty, _goShown;
    int _tick;

    public IReadOnlyList<TyrTarget> Targets => _targets.Length == 0 ? [] : _targets[_si];
    public int StandIndex => _si;
    public int StandKind => _stands[_si];
    public int StandCount => _stands.Length;
    public TyrShooter Shooter(int seat) => _sh[seat];
    public bool Over => _over;

    /// <summary>Мс поточного стенду за годинником сервера (на «Готуйсь» — від’ємні).</summary>
    public int NowMs => (int)Math.Floor((Ctx.Clock.UtcNow - _standAt).TotalMilliseconds);

    protected int Limit => Math.Min(Seats, Ctx.Players);

    /// <summary>Генератор розкладу: стіл — з <c>Ctx.Rng</c>, «Тир дня» — із сіду дня.</summary>
    protected virtual Random ScheduleRng() => Ctx.Rng;

    /// <summary>Хто грає партію (людина зі старту чи бот) і досі за столом.</summary>
    protected virtual bool InPlay(int seat) => _sh[seat].Plays && Ctx.Seated(seat);

    protected int[] Playing() => [.. Enumerable.Range(0, Limit).Where(InPlay)];

    protected string Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat) ?? $"місце {seat + 1}";

    /// <summary>Свіжа партія: розклад усіх стендів одразу (детерміновано за генератором), копії тих, хто грає.</summary>
    protected void Begin(bool[] plays)
    {
        _started = true;
        _over = false;
        _winners = [];
        var rng = ScheduleRng();
        _targets = [.. _stands.Select(s => TyrCore.Schedule(s, rng))];
        for (var i = 0; i < Seats; i++)
        {
            var sh = _sh[i] = new TyrShooter { Plays = i < plays.Length && plays[i] };
            sh.NewStand(0);
        }
        _si = -1;
        NextStand();
    }

    void NextStand()
    {
        _si++;
        _standAt = Ctx.Clock.UtcNow.AddMilliseconds(TyrCore.ReadyMs);
        _goShown = false;
        _tick = 0;
        foreach (var sh in _sh) sh.NewStand(_targets[_si].Count);
    }

    // ---------- ввід ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == LiveBots.Toggle) return Toggle(seat, payload);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");
        if (_over) return ActResult.Fail("Партію вже зіграно");
        if (seat is < 0 or >= Seats || !_sh[seat].Plays) return ActResult.Fail("Ти тут не стріляєш");
        if (action is not ("shot" or "reload")) return ActResult.Fail("Тут так не стріляють");
        if (Int(payload, "s") is not { } s || s != _si) return ActResult.Fail("Це був інший стенд");
        if (Int(payload, "t") is not { } t) return ActResult.Fail("Без часу пострілу не зарахую");
        var now = NowMs;
        if (now < 0) return ActResult.Fail("Готуйсь — ще не стріляємо");
        if (t < 0 || t >= TyrCore.StandMs || t > now + Ahead || t < now - MaxLag) return ActResult.Fail("Постріл запізнився");
        var me = _sh[seat];
        if (action == "reload")
        {
            if (!TyrCore.Reload(me, t)) return ActResult.Fail("Барабан повний або ще не час");
            _dirty = true;
            return ActResult.Done;
        }
        if (Num(payload, "x") is not { } x || Num(payload, "y") is not { } y || x < -50 || x > TyrCore.W + 50 || y < -50 || y > TyrCore.H + 50)
            return ActResult.Fail("Куди це ти цілиш?");
        if (TyrCore.Shoot(me, Targets, _si, t, x, y) is not { } shot)
            return ActResult.Fail(me.Smoked(t) ? "Дим — нічого не видно" : me.Reloading(t) ? "Перезаряджаєш" : "Зачасто");
        _dirty = true;
        if (shot.Target >= 0) OnHit(seat, Targets[shot.Target]);
        return ActResult.Done;
    }

    /// <summary>Гра бачить влучання (Глек, ачівки) — після того, як очки вже пораховано.</summary>
    protected virtual void OnHit(int seat, TyrTarget tg) { }

    protected virtual ActResult Toggle(int seat, JsonElement payload) => ActResult.Fail("Тут бота не кличуть");

    static int? Int(JsonElement p, string key) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) && double.IsFinite(d) && Math.Abs(d) < 1e7 ? (int)Math.Floor(d) : null;

    static double? Num(JsonElement p, string key) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (!_started || _over) return TickResult.None;
        var now = NowMs;
        _tick++;
        if (now < 0) return _tick % 10 == 1 ? TickResult.FrameOnly : TickResult.None;
        foreach (var sh in _sh) if (sh.Settle(Math.Min(now, TyrCore.StandMs))) _dirty = true;
        if (now < TyrCore.StandMs) BotsThink(now);
        if (now >= TyrCore.StandMs + GraceMs)
        {
            if (_si + 1 < _stands.Length)
            {
                StandOver();
                NextStand();
                return TickResult.Both;
            }
            StandOver();
            _over = true;
            End();
            return TickResult.Both;
        }
        if (!_goShown)
        {
            _goShown = true;
            _dirty = false;
            return TickResult.Both;
        }
        if (_dirty || _tick % 8 == 0)
        {
            _dirty = false;
            return TickResult.FrameOnly;
        }
        return TickResult.None;
    }

    /// <summary>Боти думають у тику й стріляють тим самим <see cref="Act"/>, що й людина.</summary>
    protected virtual void BotsThink(int now) { }

    /// <summary>Стенд скінчився (ще до переходу на наступний) — ачівки стенду.</summary>
    protected virtual void StandOver() { }

    /// <summary>Партію зіграно: Finish, нагороди.</summary>
    protected abstract void End();

    // ---------- вид і кадр ----------

    protected bool Lobby => !_started;

    protected string PhaseName => Lobby ? "lobby" : _over ? "over" : NowMs < 0 ? "ready" : "go";

    /// <summary>Спільна частина виду: поле, правила числами, рахунки кожного місця.</summary>
    protected Dictionary<string, object?> Common()
    {
        var lobby = Lobby;
        var score = new long?[Seats];
        var stands = new long[]?[Seats];
        var stats = new int[]?[Seats];
        for (var i = 0; i < Seats; i++)
        {
            var sh = _sh[i];
            if (lobby || !sh.Plays) continue;
            score[i] = sh.Score;
            stands[i] = [.. sh.StandScore.Take(_stands.Length)];
            stats[i] = [sh.Shots, sh.Hits, sh.Misses, sh.Kegs, sh.Pots, sh.Golds];
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["phase"] = PhaseName,
            ["stand"] = lobby ? 0 : _si,
            ["stands"] = _stands.Length,
            ["kinds"] = _stands.ToArray(),
            ["standNames"] = _stands.Select(s => TyrCore.StandNames[s]).ToArray(),
            ["max"] = lobby ? 0 : TyrCore.Max(Targets),
            ["score"] = score,
            ["standScores"] = stands,
            ["stats"] = stats,
            ["winners"] = _over ? (int[])_winners.Clone() : Array.Empty<int>(),
            ["winner"] = _over && _winners.Length > 0 ? _winners[0] : null,
            ["rules"] = Rules,
            ["turn"] = null,
            ["frame"] = Shot(),
        };
    }

    /// <summary>Правила числами — клієнт малює й передбачає за ними, а не за своїми константами.</summary>
    static readonly object Rules = new
    {
        w = TyrCore.W,
        h = TyrCore.H,
        standMs = TyrCore.StandMs,
        readyMs = TyrCore.ReadyMs,
        graceMs = GraceMs,
        drum = TyrCore.Drum,
        reloadMs = TyrCore.ReloadMs,
        smokeMs = TyrCore.SmokeMs,
        minGap = TyrCore.MinGap,
        slack = TyrCore.Slack,
        maxLag = MaxLag,
        ahead = Ahead,
        points = TyrCore.Points,
        radius = TyrCore.Radius,
        shelfY = TyrCore.ShelfY,
        laneY = TyrCore.LaneY,
        slotX = Enumerable.Range(0, TyrCore.Slots).Select(TyrCore.SlotX).ToArray(),
    };

    public override object? Frame() => Shot();

    /// <summary>
    /// Кадр (spec §4): фаза, стенд, мить стенду <c>now</c> (мс; на «Готуйсь» — від’ємна), копія кожного місця
    /// <c>p[i] = [рахунок, рахунок стенду, набої, кінець перезарядки, кінець диму, n]</c>, збиті ним мішені <c>h[i]</c>
    /// і вікно розкладу <c>tg</c> = [id, вид, t0, тривалість, x, y, vx, r]. Таємного нема: розклад однаковий для всіх.
    /// </summary>
    protected object Shot()
    {
        var lobby = Lobby;
        var now = lobby ? 0 : NowMs;
        var p = new long[]?[Seats];
        var h = new int[]?[Seats];
        var tg = new List<int[]>();
        if (!lobby)
        {
            for (var i = 0; i < Seats; i++)
            {
                var sh = _sh[i];
                if (!sh.Plays) continue;
                p[i] = [sh.Score, sh.StandScore[_si], sh.Ammo, sh.ReloadAt, Math.Max(-1, sh.SmokeUntil), sh.N];
                h[i] = [.. sh.HitIds];
            }
            if (!_over)
                foreach (var t in Targets)
                {
                    if (t.T0 > now + LookMs) break;
                    if (t.End < now - TailMs) continue;
                    tg.Add([t.Id, t.Kind, t.T0, t.Dur, t.X, t.Y, t.Vx, t.R]);
                }
        }
        return new
        {
            ph = lobby ? PhLobby : _over ? PhOver : now < 0 ? PhReady : PhGo,
            st = lobby ? 0 : _si,
            k = _stands[lobby ? 0 : _si],
            now,
            end = TyrCore.StandMs,
            p,
            h,
            tg,
        };
    }
}

/// <summary>Ярмарковий тир за столом: 1–8 стрільців, три стенди, соло з ботом, режим вечірки.</summary>
public sealed class Tyr : TyrBase, IPartyMinigame
{
    public override GameInfo Info { get; } = new(
        "tyr", "Ярмарковий тир", "ярмарковий тир", GameGroup.Live, 1, Seats, TickMs: 50,
        Start: StartMode.ByHost, Options: [LiveBots.LevelOption],
        Hint: "Вискакують глеки, пливуть качки, на мить блисне золотий — клацай! Мішені в усіх однакові, тож вирішує око, а не пінг. Діжку з порохом і бабин горщик не чіпай. Самому — з 🤖 ботом");

    /// <summary>Скільки ботів, коли людина сама: один — дуель на влучність, рахунок суперника видно знизу.</summary>
    public const int SoloBots = 1;

    readonly SoloBot _solo = new();
    PartyMode? _party;
    int[] _bots = [];
    readonly TyrBot?[] _brain = new TyrBot?[Seats];
    bool _botGame;
    int _startPlayers;
    bool _potSaid;
    LiveBots.Level _level;

    public bool Party => _party is not null;
    public bool BotGame => _botGame;
    public IReadOnlyList<int> Bots => _bots;

    public string Howto => "Клацай по глеках і качках, поки не сховались: золотий — +3. Діжку з порохом і бабин горщик не чіпай! "
        + "Мишка чи дотик — постріл, правий клік або ⟳ — перезарядка (6 набоїв); пад — стік і A, X — перезарядка";
    /// <summary>3 с «Готуйсь» + стенд 45 с + 0,4 с на запізнілі постріли — з запасом до 60 с.</summary>
    public int PartyCapMs => 60_000;
    public int PartyMin => 2;
    public int PartyMax => Seats;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _solo.Configure(options);
        _party = PartyMode.Read(options);
        // Вечірка — один стенд «усе разом»: найвеселіший і вкладається в хвилину.
        if (_party is not null) _stands = [TyrCore.Mix];
    }

    public override bool ActsInLobby => true;

    public override string? CanStart() => _party is not null ? null : _solo.CanStart(Ctx, Seats);

    protected override ActResult Toggle(int seat, JsonElement payload) =>
        _party is not null ? ActResult.Fail("У вечірці ботів садить вечірка")
        : _started && !_over ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, Seats);

    bool IsBot(int seat) => Array.IndexOf(_bots, seat) >= 0 && !Ctx.Seated(seat);

    int[] BotSeats() => _solo.Active(Ctx, Seats) ? [.. Enumerable.Range(0, Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    public override string? SeatBot(int seat) =>
        !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0 ? LiveBots.Name : null;

    /// <summary>У вечірці місце, з якого людина пішла посеред стенду, дограє свій рахунок як є (стоїть, не стріляє).</summary>
    protected override bool InPlay(int seat) => Shooter(seat).Plays && (Ctx.Seated(seat) || IsBot(seat) || _party is not null);

    public override void Start()
    {
        _bots = _party is { } pm ? [.. pm.Bots.Where(s => s < Limit && !Ctx.Seated(s))] : BotSeats();
        _botGame = _bots.Length > 0;
        _level = _party?.Level ?? _solo.Level;
        Array.Clear(_brain);
        foreach (var b in _bots) _brain[b] = new TyrBot(_level);
        _startPlayers = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        _potSaid = false;
        var plays = new bool[Seats];
        for (var i = 0; i < Seats; i++)
            plays[i] = _party is not null ? i < Limit : Ctx.Seated(i) || Array.IndexOf(_bots, i) >= 0;
        Begin(plays);
        if (_party is null && Ctx.Round == 1)
            Ctx.Say(_botGame ? "🎯 Ярмарок відкрито! Бот уже цілиться — не барись" : "🎯 Ярмарок відкрито! Набоїв по шість, бабині горщики не чіпати");
    }

    protected override void BotsThink(int now)
    {
        foreach (var s in _bots)
        {
            if (Ctx.Seated(s) || _brain[s] is not { } bot) continue;
            var me = Shooter(s);
            switch (bot.Think(now, me, Targets, Ctx.Rng, out var x, out var y))
            {
                case TyrBot.Move.Shot:
                    Act(s, "shot", JsonSerializer.SerializeToElement(new { s = StandIndex, t = now, x, y }));
                    break;
                case TyrBot.Move.Reload:
                    Act(s, "reload", JsonSerializer.SerializeToElement(new { s = StandIndex, t = now }));
                    break;
            }
        }
    }

    protected override void OnHit(int seat, TyrTarget tg)
    {
        if (tg.Kind != TyrCore.Pot || _potSaid) return;
        _potSaid = true;
        Ctx.Say($"🏺 {Name(seat)} бахнув у бабин горщик. Хто бахнув у бабин горщик — до баби на розмову");
    }

    bool Awards => _party is null && !_botGame && _startPlayers >= 2;

    protected override void StandOver()
    {
        if (!Awards) return;
        foreach (var s in Playing())
        {
            var sh = Shooter(s);
            var si = StandIndex;
            // Снайпер: стенд без жодного промаху й без «поганих» мішеней, і щоб справді стріляв (10+ пострілів).
            if (sh.StandShots[si] >= 10 && sh.StandMisses[si] == 0 && sh.StandBad[si] == 0) Ctx.Award(s, 0, "ach:tyr-sniper");
            if (StandKind == TyrCore.Mix)
            {
                var golds = Targets.Count(t => t.Kind == TyrCore.Gold);
                if (golds >= 3 && sh.StandGolds[si] == golds) Ctx.Award(s, 0, "ach:tyr-gold");
            }
        }
    }

    protected override void End()
    {
        if (_party is not null) { PartyOver(); return; }
        var playing = Playing();
        var best = playing.Length == 0 ? 0 : playing.Max(s => Shooter(s).Score);
        // Усі однаково — нічия (на одного стрільця нічиєї нема: він і переміг).
        _winners = playing.Length == 0 || (playing.Length > 1 && playing.All(s => Shooter(s).Score == best)) ? []
            : [.. playing.Where(s => Shooter(s).Score == best)];
        if (Awards)
            foreach (var s in playing)
                if (Shooter(s).Kegs == 0 && Shooter(s).Score >= DryMin) Ctx.Award(s, 0, "ach:tyr-dry");
        var scores = playing.Where(Ctx.Seated).ToDictionary(s => s, s => Shooter(s).Score);
        var log = Journal(playing);
        if (_winners.Length > 0 && _party is null)
            Ctx.Say($"🎯 Найвлучніший на ярмарку — {Name(_winners[0])}: {Shooter(_winners[0]).Score} {Ochok(Shooter(_winners[0]).Score)}");
        if (!_botGame)
        {
            Ctx.Finish(_winners, log, scores);
            return;
        }
        var people = _winners.Where(Ctx.Seated).ToArray();
        var verdict = people.Length > 0 ? $"🏆 {Ctx.NickOf(people[0])} — перемога над {LiveBots.Of(_level)} ботом"
            : _winners.Length > 0 ? $"🤖 Тир узяв {Name(_winners[0])}" : null;
        Ctx.Finish(people, log, scores, verdict);
    }

    /// <summary>«Порох сухий» — лише для того, хто справді стріляв, а не простояв партію.</summary>
    public const int DryMin = 40;

    public IReadOnlyDictionary<int, long> PartyScores()
    {
        var r = new Dictionary<int, long>(Ctx.Players);
        for (var s = 0; s < Ctx.Players; s++) r[s] = s < Seats && _started ? Shooter(s).Score : 0;
        return r;
    }

    void PartyOver()
    {
        var scores = PartyScores();
        var best = scores.Count == 0 ? 0 : scores.Values.Max();
        _winners = [.. scores.Where(kv => kv.Value == best).Select(kv => kv.Key).Order()];
        Ctx.Finish(_winners, Journal([.. Enumerable.Range(0, Limit)]), scores);
    }

    /// <summary>«Ярмарковий тир: Оля 57 : Петро 41» — від найвлучнішого.</summary>
    string Journal(int[] playing)
    {
        var order = playing.OrderByDescending(s => Shooter(s).Score).ThenBy(s => s);
        var line = $"{Info.Title}: {string.Join(" : ", order.Select(s => $"{Name(s)} {Shooter(s).Score}"))}";
        return _winners.Length == 0 ? line + " — нічия" : line;
    }

    public static string Ochok(long n)
    {
        long d = Math.Abs(n) % 10, h = Math.Abs(n) % 100;
        return d == 1 && h != 11 ? "очко" : d is >= 2 and <= 4 && (h < 12 || h > 14) ? "очки" : "очок";
    }

    /// <summary>
    /// Хтось устав посеред партії: його копія зупиняється, решта стріляє далі. Не лишилось жодної людини — партію
    /// не дограли. У вечірці <c>OnLeave</c> не кличуть (там місце просто стоїть).
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (!_started || Over || _party is not null) return;
        var left = Enumerable.Range(0, Seats).Where(s => s != seat && Ctx.Seated(s) && Shooter(s).Plays).ToArray();
        if (left.Length > 0)
        {
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} пішов зі стрільбища — решта стріляє далі");
            return;
        }
        _over = true;
        _winners = [];
        Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(seat)} пішов зі стрільбища, партію не дограли",
            left.ToDictionary(s => s, s => Shooter(s).Score));
    }

    public override object View(int? seat)
    {
        var v = Common();
        v["mode"] = _party is not null ? "party" : "table";
        v["howto"] = _party is not null ? Howto : null;
        v["botOffer"] = _party is null && _solo.Offer(Ctx, Seats);
        v["botWanted"] = _solo.Wanted;
        v["botLvl"] = LiveBots.Key(_party?.Level ?? _solo.Level);
        v["bot"] = Lobby ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray();
        return v;
    }
}
