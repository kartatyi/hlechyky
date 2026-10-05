using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Глечикова вечірка (specs/vechirka.md): настільна гра-дошка на 2–8 (люди + іменні боти лобі), між колами —
/// міні-ігри каталогу. Тут — кімната: лобі, крісла ↔ P (за ніком, §1.2), повернення (К1), авто-пауза, вид і кадр,
/// репліки Глека. Правила — у <see cref="VechirkaCore"/>.
/// </summary>
public sealed class Vechirka : Game
{
    public static readonly GameOption LenOption = new("len", "Вечір",
        [("30", "≈ 30 хв"), ("45", "≈ 45 хв"), ("60", "≈ 60 хв")], "45");
    public static readonly GameOption MinisOption = new("minis", "Міні-ігри",
        [("move", "🏃 рухливі"), ("brain", "🧠 кмітливі"), ("tap", "👆 на реакцію")], "move,brain,tap", Multi: true);
    public static readonly GameOption BotLevelOption = new("botlvl", "🤖 Боти",
        [("easy", "легкі"), ("normal", "звичайні"), ("hard", "сильні")], "normal");
    public static readonly GameOption VoiceOption = new("voice", "Голос Глека",
        [("ostap", "Остап"), ("polina", "Поліна"), ("none", "Без голосу")], "ostap");

    public override GameInfo Info { get; } = new(
        "vechirka", "Глечикова вечірка", "Глечикову вечірку", GameGroup.Party, 1, 8, TickMs: 20, Start: StartMode.ByHost,
        Options: [LenOption, MinisOption, BotLevelOption, VoiceOption],
        Hint: "Настільна вечірка на 2–8: ходиш селом, збираєш шеляги, купуєш у Дядька Глека золоті глеки, а між колами — "
            + "міні-ігри з нашого каталогу. Пательня, вила й шлагбаум — додаються. Боти — «🤖 + бот».");

    public const int Seats = 8;
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(15);
    const int SayGapMs = 20_000;
    /// <summary>Прогрів озвучки на старті (§13): лише рядки ✱-пулів без підстановок, не більше стількох.</summary>
    public const int WarmMax = 40;
    /// <summary>Рядок із ніком озвучується на льоту: не встиг за стільки — лишається текстом без звуку.</summary>
    public const int OnFlyMs = 1_500;

    readonly List<string> _lobbyBots = [];
    VechirkaCore? _core;
    /// <summary>Крісло кімнати кожного P (null — бот або відпав).</summary>
    int?[] _seat = [];
    /// <summary>P, що дивляться поточну міні-гру глядачами (повернулись посеред неї).</summary>
    readonly HashSet<int> _mgWatch = [];
    readonly List<(int Seq, int I, string K)> _emo = [];
    readonly Dictionary<int, DateTimeOffset> _emoAt = [];
    int _emoSeq;
    VechirkaLines _lines = VechirkaLines.Book;
    (int Id, string Text, string? Url)? _say;
    int _sayId;
    /// <summary>✱-репліка ще озвучується: до <see cref="OnFlyMs"/> від <see cref="_sayAt"/> підхоплюємо готовий кліп.</summary>
    bool _sayPending;
    DateTimeOffset _sayAt;
    ISvoyaVoice _voice = NoVoice.Instance;
    string _voiceName = "ostap";
    DateTimeOffset _lastSay = DateTimeOffset.MinValue;
    bool _hostPause, _emptySaid;
    /// <summary>Хто поставив паузу господаря: відпав він — пауза знімається (§18).</summary>
    int? _pauseBy;
    ILogger? _log;
    DateTimeOffset _errAt;
    string _sig = "";

    /// <summary>Мізки ботів — замінні для тестів.</summary>
    public Func<IVechirkaBrain> BrainFactory { get; set; } = () => new VechirkaBot();
    /// <summary>Міні-ігри — замінні для тестів (S1.4 — справжній хост).</summary>
    public Func<Vechirka, ulong, IMgRunner> RunnerFactory { get; set; } = (g, _) => new VechirkaHostMg(g);
    public Func<IReadOnlyList<VechirkaPoolEntry>> PoolFactory { get; set; } = () => VechirkaPool.Available;

    public VechirkaCore? Core => _core;
    public override bool ActsInLobby => true;
    public override TimeSpan HoldEmpty => Hold;

    // ---------- лобі ----------

    int Humans => Enumerable.Range(0, Info.MaxPlayers).Count(Ctx.Seated);

    /// <summary>Людей стало більше, ніж вміщує, — зайвих ботів знімаємо самі.</summary>
    void TrimBots()
    {
        while (_lobbyBots.Count > 0 && Humans + _lobbyBots.Count > Seats) _lobbyBots.RemoveAt(_lobbyBots.Count - 1);
    }

    public override string? CanStart()
    {
        TrimBots();
        var n = Humans + _lobbyBots.Count;
        return n < 2 ? "Треба хоч одного суперника — поклич бота «🤖 + бот»" : n > Seats ? "Забагато гостей" : null;
    }

    public override void Start()
    {
        TrimBots();
        var o = Ctx.Options;
        var st = new VechirkaState
        {
            Len = int.TryParse(o.GetValueOrDefault("len"), out var len) && len is 30 or 45 or 60 ? len : 45,
            Minis = [.. GameOption.Split(o.GetValueOrDefault("minis") ?? "move,brain,tap")],
            Level = (o.GetValueOrDefault("botlvl") ?? "normal") switch
            {
                "easy" => LiveBots.Level.Easy, "hard" => LiveBots.Level.Hard, _ => LiveBots.Level.Normal,
            },
        };
        var seats = new List<int?>();
        for (var s = 0; s < Info.MaxPlayers; s++)
            if (Ctx.Seated(s) && Ctx.NickOf(s) is { } nick)
            {
                st.P.Add(new VechirkaPlayer { Nick = nick, Name = nick });
                seats.Add(s);
            }
        foreach (var b in _lobbyBots) { st.P.Add(new VechirkaPlayer { Bot = true, Name = b }); seats.Add(null); }
        _seat = [.. seats];
        _mgWatch.Clear(); _emo.Clear(); _emoAt.Clear(); _say = null; _hostPause = false; _emptySaid = false;
        _lines = VechirkaLines.Book;
        _sayPending = false;
        _voice = Ctx.Services.GetService<ISvoyaVoice>() ?? NoVoice.Instance;
        _log = Ctx.Services.GetService<ILogger<Vechirka>>();
        _pauseBy = null;
        _voiceName = o.GetValueOrDefault("voice") is "polina" or "none" ? o["voice"] : "ostap";
        if (VoiceOn) Prepare(_lines.Warm(WarmMax), false);
        var seed = unchecked((ulong)Ctx.Rng.NextInt64());
        _lines.Seeded(unchecked((int)(seed >> 7)));
        var runner = RunnerFactory(this, seed ^ 0x5EEDUL);
        _core = new VechirkaCore(VechirkaMap.Load(st.Map), st, runner, PoolFactory()) { Brain = BrainFactory(), OnError = Oops };
        _core.Start(seed, Ctx.Clock.UtcNow);
        Flush();
    }

    // ---------- крісла ↔ P ----------

    public int? POf(int seat)
    {
        for (var i = 0; i < _seat.Length; i++) if (_seat[i] == seat) return i;
        return null;
    }

    public int? SeatOfP(int i) => i >= 0 && i < _seat.Length ? _seat[i] : null;

    public override bool LateJoin(string nick) =>
        _core is { S.Done: false } c && c.S.P.Any(p => !p.Bot && p.Away && Same(p.Nick, nick));

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public override void OnJoin(int seat)
    {
        if (_core is not { } c) return;
        var nick = Ctx.NickOf(seat);
        var i = c.S.P.FindIndex(p => !p.Bot && Same(p.Nick, nick));
        if (i < 0) return;
        var p = c.S.P[i];
        _seat[i] = seat;
        p.Away = false; p.Auto = false; p.Misses = 0;
        if (c.S.Phase == "mg") _mgWatch.Add(i);
        Say("back", ("nick", p.Name));
        c.Replan(Ctx.Clock.UtcNow);
        _sig = "";
    }

    /// <summary>Вийшов — за нього ходить бот. Вечірку це не кінчає ніколи (§1.2).</summary>
    public override void OnLeave(int seat)
    {
        if (_core is not { } c || POf(seat) is not { } i) return;
        _seat[i] = null;
        var p = c.S.P[i];
        p.Away = true;
        if (c.S.Phase == "mg") _mgWatch.Add(i);
        Say("away", ("nick", p.Name));
        // господар поставив паузу й пішов — не тримаємо стіл до 10 хв (R1 m1)
        if (_pauseBy == seat && _hostPause)
        {
            _hostPause = false; _pauseBy = null;
            if (c.S.Paused == "host") { c.SetPause(null); Say("unpause"); }
        }
        c.Replan(Ctx.Clock.UtcNow);
        _sig = "";
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        var now = Ctx.Clock.UtcNow;
        if (_core is not { } c || c.S.Done)
        {
            if (action != LiveBots.Toggle) return ActResult.Fail("Вечірка ще не почалась");
            if (seat != Ctx.HostSeat) return ActResult.Fail("Ботів кличе господар столу");
            var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("on", out var v)
                ? v.ValueKind == JsonValueKind.True : _lobbyBots.Count == 0;
            if (on)
            {
                if (Humans + _lobbyBots.Count >= Seats) return ActResult.Fail("Місць більше нема");
                _lobbyBots.Add(VechirkaRules.BotNames.First(b => !_lobbyBots.Contains(b)));
            }
            else if (_lobbyBots.Count > 0) _lobbyBots.RemoveAt(_lobbyBots.Count - 1);
            return ActResult.Done;
        }
        if (POf(seat) is not { } i) return ActResult.Fail("Ти в цій вечірці не граєш");
        switch (action)
        {
            case "pause":
            {
                if (seat != Ctx.HostSeat) return ActResult.Fail("Паузу ставить господар");
                if (c.S.Phase is "mg" or "done") return ActResult.Fail("Зараз не можна");
                var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
                _hostPause = on;
                _pauseBy = on ? seat : null;
                if (c.S.Paused != "empty") { c.SetPause(on ? "host" : null); Say(on ? "pause" : "unpause"); }
                _sig = "";
                return ActResult.Done;
            }
            case "emo":
            {
                var k = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("k", out var kv) ? kv.GetString() : null;
                if (k is null || !VechirkaRules.Emo.Contains(k)) return ActResult.Fail("Такої реакції нема");
                // частіше раз на 2 с — мовчки ігноруємо (не помилка)
                if (_emoAt.TryGetValue(i, out var at) && now - at < TimeSpan.FromSeconds(2)) return ActResult.Done;
                _emoAt[i] = now;
                _emo.Add((++_emoSeq, i, k));
                while (_emo.Count > 8) _emo.RemoveAt(0);
                _sig = "";
                return ActResult.Done;
            }
            case "mg" when _mgWatch.Contains(i):
                return ActResult.Fail("Ти в цій міні-грі не граєш");
        }
        try { c.Act(i, action, payload, now); }
        catch (GameError e) { Flush(); return ActResult.Fail(e.Message); }
        catch (Exception e) { Oops(e); Flush(); return ActResult.Fail("Глек спіткнувся — спробуй ще"); }
        Flush();
        return ActResult.Done;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (_core is not { } c || c.S.Done) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        // Авто-пауза «стіл порожній»: жодної присутньої людини — фази дошки стоять, міні-гра догравається.
        var anyone = _seat.Any(s => s is { } x && Ctx.Seated(x));
        if (!anyone && c.S.Paused != "empty")
        {
            c.SetPause("empty");
            if (!_emptySaid) { _emptySaid = true; Say("empty"); }
        }
        else if (anyone && c.S.Paused == "empty")
        {
            c.SetPause(_hostPause ? "host" : null);
            _emptySaid = false;
        }
        if (c.S.Paused == "host") _hostPause = true;
        else if (c.S.Paused is null) _hostPause = false;
        try { c.Advance(now); }
        catch (Exception e)
        {
            // будь-який збій ядра — не кінець вечора: лог і типове рішення фази (R1 M4)
            Oops(e);
            try { c.Rescue(now); } catch (Exception e2) { Oops(e2); }
        }
        var sub = c.LastMgTick;
        c.LastMgTick = TickResult.None;
        if (c.S.Phase != "mg") _mgWatch.Clear();
        Flush();
        var sig = Signature(c);
        var changed = sig != _sig | PollVoice(now);
        _sig = sig;
        if (changed) return TickResult.Both;
        return sub;
    }

    /// <summary>Неочікуваний виняток — у лог, не частіше раз на 30 с (щоб збій на кожному тику не залив лог).</summary>
    void Oops(Exception e)
    {
        var now = Ctx.Clock.UtcNow;
        if (now - _errAt < TimeSpan.FromSeconds(30)) return;
        _errAt = now;
        _log?.LogError(e, "Глечикова вечірка: збій у фазі {Phase}", _core?.S.Phase);
    }

    static string Signature(VechirkaCore c)
    {
        var s = c.S;
        return $"{s.Seq}|{s.Phase}|{s.Cur}|{s.Pr?.Kind}|{s.Pr?.Who}|{s.Paused}|{s.Until?.Ticks}|{s.Busy is null}|{s.M?.Ready.Count}|{s.M?.Bets.Count}|{s.L?.Chosen.Count}|{s.F?.Step}|{s.Log.Count}|{s.P.Count(p => p.Auto)}|{s.P.Count(p => p.Away)}|{s.Am is null}|{s.Pk?.Chosen}";
    }

    /// <summary>Події ядра — назовні: репліки в балачку, кінець.</summary>
    void Flush()
    {
        if (_core is not { } c) return;
        foreach (var o in c.Out)
            switch (o.Kind)
            {
                case "say": Say(o.Key, o.Args); break;
                case "finish": Finish(c); break;
            }
        c.Out.Clear();
    }

    void Say(string pool, params (string K, string V)[] args) =>
        Say(pool, args.Length == 0 ? null : args.ToDictionary(a => a.K, a => a.V));

    void Say(string pool, IReadOnlyDictionary<string, string>? args)
    {
        var now = Ctx.Clock.UtcNow;
        var star = VechirkaLines.Starred.Contains(pool);
        if (!star && now - _lastSay < TimeSpan.FromMilliseconds(SayGapMs)) return;
        if (_lines.Render(pool, args) is not { } text) return;
        _lastSay = now;
        string? url = null;
        _sayPending = false;
        if (star && VoiceOn)
        {
            url = Clip(text);
            // ще не готова (рядок із ніком чи прогрів не встиг) — у чергу першою, текст показуємо одразу
            if (url is null) { Prepare([text], true); _sayPending = true; _sayAt = now; }
        }
        _say = (++_sayId, text, url);
        Ctx.Say(text);
    }

    /// <summary>Озвучуємо лише ✱-пули й лише коли голос є і не вимкнений опцією «Без голосу».</summary>
    public bool VoiceOn => _voiceName != "none" && _voice.Enabled;

    void Prepare(IEnumerable<string> texts, bool urgent)
    {
        try { _voice.Prepare(_voiceName, texts, urgent); }
        catch (Exception) { /* голос — прикраса: збій озвучки не валить вечірку */ }
    }

    string? Clip(string text)
    {
        try { return _voice.Ready(_voiceName, text)?.Url; }
        catch (Exception) { return null; }
    }

    /// <summary>Підхопити кліп ✱-репліки, що озвучувалась на льоту; true — вид змінився.</summary>
    bool PollVoice(DateTimeOffset now)
    {
        if (!_sayPending || _say is not { } sy) return false;
        if (now - _sayAt > TimeSpan.FromMilliseconds(OnFlyMs)) { _sayPending = false; return false; }
        if (Clip(sy.Text) is not { } url) return false;
        _sayPending = false;
        _say = sy with { Url = url };
        return true;
    }

    void Finish(VechirkaCore c)
    {
        var scores = new Dictionary<int, long>();
        var present = new List<int>();
        for (var i = 0; i < c.N; i++)
        {
            if (_seat[i] is not { } seat || !Ctx.Seated(seat)) continue;
            scores[seat] = c.Rank(i);
            present.Add(i);
        }
        // Переможці кімнати — найкращі з присутніх людей. Порожній winners каркас вважає нічиєю (напис «Нічия»
        // і виплата за нічию), тож коли перше місце в бота, перемога все одно дістається найкращій людині,
        // а хто справді голова вечірки — пишемо у вердикті (R2 M2).
        var best = present.Count == 0 ? 0 : present.Min(c.PlaceOf);
        var winners = present.Where(i => c.PlaceOf(i) == best).Select(i => _seat[i]!.Value).ToArray();
        string? verdict = null;
        var tops = Enumerable.Range(0, c.N).Where(i => c.PlaceOf(i) == 1).ToList();
        if (present.Count > 0 && tops.Any(i => c[i].Bot))
            verdict = $"🏆 Голова вечірки — {string.Join(", ", tops.Select(i => c[i].Name))}"
                + (tops.All(i => c[i].Bot) ? $" · з людей найкраще — {string.Join(", ", present.Where(i => c.PlaceOf(i) == best).Select(i => c[i].Name))}" : "");
        var log = "🎉 Глечикова вечірка: " + string.Join(", ",
            Enumerable.Range(0, c.N).OrderBy(c.PlaceOf).Select(i => $"{c[i].Name} — {c[i].Gleks} 🏺"));
        Ctx.Finish(winners, log, scores, verdict);
        Rewards(c);
    }

    /// <summary>
    /// Черепки за місце серед людей і ачівки (§10) — лише коли людей за столом ≥ 2. У причині — номер партії
    /// кімнати: «Ще раз» за тим самим столом платить заново (ref нагороди містить причину).
    /// </summary>
    void Rewards(VechirkaCore c)
    {
        var humans = Enumerable.Range(0, c.N).Where(i => !c[i].Bot && _seat[i] is { } s && Ctx.Seated(s)).ToList();
        if (humans.Count < 2) return;
        int[] bonus = humans.Count >= 3 ? [20, 10, 5] : [20];
        foreach (var i in humans)
        {
            var seat = _seat[i]!.Value;
            var place = 1 + humans.Count(j => c.Rank(j) > c.Rank(i));
            if (place <= bonus.Length) Ctx.Award(seat, bonus[place - 1], $"vechirka:place:{Ctx.Round}");
            var p = c[i];
            var won = c.PlaceOf(i) == 1;
            if (won && humans.Count >= 3) Ctx.Award(seat, 0, "ach:vechirka-win");
            if (won && p.S.LastAtHalf) Ctx.Award(seat, 0, "ach:vechirka-comeback");
            if (p.S.Bought >= 5) Ctx.Award(seat, 0, "ach:vechirka-gleks5");
            if (p.S.Pans >= 3) Ctx.Award(seat, 0, "ach:vechirka-pan3");
            if (p.MgWins >= 5) Ctx.Award(seat, 0, "ach:vechirka-king");
            if (p.S.Banks >= 3) Ctx.Award(seat, 0, "ach:vechirka-bank");
            if (p.S.Ferries >= 3) Ctx.Award(seat, 0, "ach:vechirka-ferry");
        }
    }

    // ---------- вид (§14.1) ----------

    public override object View(int? seat)
    {
        if (_core is not { } c)
        {
            TrimBots();
            var n = Humans + _lobbyBots.Count;
            var len = int.TryParse(Ctx.Options.GetValueOrDefault("len"), out var l) ? l : 45;
            return new
            {
                phase = "lobby",
                lobby = new { bots = _lobbyBots.Select(b => new { name = b }), rounds = VechirkaRules.Rounds(len, Math.Max(2, n)) },
            };
        }
        return VechirkaView.Build(this, c, seat is { } s ? POf(s) : null, Ctx.Clock.UtcNow);
    }

    public override object? Frame() =>
        _core is { S.Phase: "mg" } c && c.S.M is { Running: true } ? new { mg = c.Mg.Frame() } : null;

    public IReadOnlyList<(int Seq, int I, string K)> Emo => _emo;
    public (int Id, string Text, string? Url)? LastSay => _say;
    DateTimeOffset _lastSubSay = DateTimeOffset.MinValue;

    /// <summary>Репліка міні-гри: не частіше раз на 20 с (за вечір їх десятки, балачка не гумова).</summary>
    public bool SubSay(string text)
    {
        var now = Ctx.Clock.UtcNow;
        if (now - _lastSubSay < TimeSpan.FromMilliseconds(SayGapMs)) return false;
        _lastSubSay = now;
        Ctx.Say(text);
        return true;
    }

    public bool Watching(int i) => _mgWatch.Contains(i);
    public string? NickAt(int i) => _seat[i] is { } s && Ctx.Seated(s) ? Ctx.NickOf(s) : null;

    /// <summary>v1 без відновлення після рестарту (§17): Save ядра є, але кімната нічого не пише.</summary>
    public override string? Save() => null;
}
