using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Турнір стрільців (п. 66): від трьох до восьми за столом, сітка на вибування. Пари дуелять по черзі — той самий
/// <see cref="DuelBout"/>, що й у Дуелі, — решта дивиться й вболіває. Хто виграв фінал — «шериф вечора» в Журналі.
/// <para>
/// Окрема гра, а не режим Дуелі: дуель на двох має Ело, ставки й ачівки, а платформа знає їх лише для ігор рівно
/// на двох. Сітку тягнемо жеребом (<c>Ctx.Rng</c>); коли людей не степінь двійки, частина першого кола проходить без бою.
/// </para>
/// </summary>
public sealed class DuelCup : Game
{
    public const int Seats = 8;
    /// <summary>Перед першим раундом кожного матчу «Готуйсь…» довше: встигнути прочитати, хто з ким.</summary>
    public const int IntroMs = 1500;

    public override GameInfo Info { get; } = new(
        "duelcup", "Турнір стрільців", "турнір стрільців", GameGroup.Live, 3, Seats, TickMs: Duel.TickMs,
        Start: StartMode.ByHost, Score: ScoreOrder.LowerIsBetter, Client: "duel",
        Options:
        [
            new GameOption("match", "Матч", [("2", "до 2 перемог"), ("3", "до 3 перемог")], "2"),
            DuelKit.BaitOption, DuelKit.SignalOption, DuelKit.PingOption,
        ],
        Hint: "Від трьох до восьми стрільців: сітка на вибування, пари дуелять по черзі, решта вболіває. Хто виграв фінал — шериф вечора");

    static readonly string[] Names = ["шериф", "бандит", "шулер", "гробар", "ковбой", "мисливець", "коваль", "дяк"];
    public override string SeatName(int seat) => seat is >= 0 and < Seats ? Names[seat] : base.SeatName(seat);

    sealed class Match
    {
        public int? A, B, Winner;
        public int SA, SB;
        public bool Done, Walk;
    }

    DuelBout? _bout;
    DuelKit? _kit;
    DuelBout Bout => _bout ??= new DuelBout(Ctx);
    DuelKit Kit => _kit ??= new DuelKit(this, Seats);

    int _need = 2;
    readonly List<List<Match>> _rounds = [];
    Match? _cur;
    int? _champ;
    bool _over;
    readonly bool[] _gone = new bool[Seats];
    /// <summary>Хтось устав між тиками — наступний тик шле і кадр, і види.</summary>
    bool _dirty;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _need = options.TryGetValue("match", out var m) && m == "3" ? 3 : 2;
        Kit.Configure(options);
    }

    public override void Start()
    {
        Kit.Reset();
        Kit.Wire(Bout);
        Bout.WinsNeeded = _need;
        Array.Clear(_gone);
        _rounds.Clear();
        _cur = null;
        _champ = null;
        _over = false;
        _dirty = false;

        var players = new List<int>();
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s)) players.Add(s);
        for (var i = players.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (players[i], players[j]) = (players[j], players[i]);
        }
        var size = 2;
        while (size < players.Count) size *= 2;
        var byes = size - players.Count;
        var first = new List<Match>();
        var k = 0;
        for (var i = 0; i < size / 2; i++)
        {
            // Без бою проходять перші в жеребі: так вільні місця ніколи не зустрічаються між собою.
            var a = k < players.Count ? players[k++] : (int?)null;
            var b = i < byes || k >= players.Count ? (int?)null : players[k++];
            first.Add(new Match { A = a, B = b });
        }
        _rounds.Add(first);
        Proceed(Ctx.Clock.UtcNow);
    }

    /// <summary>
    /// Знайти наступний матч, що справді гратиметься: без суперника (жереб, хтось пішов) — прохід без бою. Коло
    /// дограно — складаємо наступне з переможців. Лишився один — шериф вечора.
    /// </summary>
    void Proceed(DateTimeOffset now)
    {
        _cur = null;
        while (true)
        {
            var round = _rounds[^1];
            var m = round.Find(x => !x.Done);
            if (m is null)
            {
                if (round.Count == 1)
                {
                    Champion(round[0]);
                    return;
                }
                var next = new List<Match>();
                for (var i = 0; i + 1 < round.Count; i += 2)
                    next.Add(new Match { A = round[i].Winner, B = round[i + 1].Winner });
                _rounds.Add(next);
                continue;
            }
            var aOk = m.A is { } a && !_gone[a];
            var bOk = m.B is { } b && !_gone[b];
            if (!aOk || !bOk)
            {
                m.Done = true;
                m.Walk = true;
                m.Winner = aOk ? m.A : bOk ? m.B : null;
                continue;
            }
            _cur = m;
            Bout.Seats[0] = m.A!.Value;
            Bout.Seats[1] = m.B!.Value;
            Bout.Reset(now, IntroMs);
            return;
        }
    }

    void Champion(Match final)
    {
        _over = true;
        _champ = final.Winner;
        Bout.Finish();
        var log = final.Winner is not { } w
            ? $"{Info.Title}: фінал не відбувся — шерифа вечора нема"
            : final.Walk || final.A is null || final.B is null
                ? $"{Info.Title}: 🤠 шериф вечора — {Ctx.NickOf(w)}! Фінал без бою"
                : $"{Info.Title}: 🤠 шериф вечора — {Ctx.NickOf(w)}! Фінал: {Ctx.NickOf(final.A.Value)} {final.SA}:{final.SB} {Ctx.NickOf(final.B.Value)}";
        Ctx.Finish(final.Winner is { } c ? [c] : [], log);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "pong") { Kit.Pong(seat, payload); return ActResult.Done; }
        if (action != "shoot") return ActResult.Fail("Тут так не ходять");
        if (_over) return ActResult.Fail("Турнір зіграно, тисни «Ану ще раз»");
        var side = Array.IndexOf(Bout.Seats, seat);
        if (_cur is null || side < 0) return ActResult.Fail("Зараз стріляються інші — вболівай!");
        return Bout.Shoot(side, Ctx.Clock.UtcNow, "Матч зіграно — мить, і наступна пара");
    }

    public override TickResult Tick()
    {
        if (_over) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        if (_dirty)
        {
            _dirty = false;
            return Kit.Mark(TickResult.Both, now);
        }
        var b = Bout;
        switch (b.Tick(now))
        {
            case BoutTick.Frame:
                return Kit.Mark(TickResult.FrameOnly, now);
            case BoutTick.Announced:
                return Kit.Mark(TickResult.Both, now);
            case BoutTick.PauseOver:
                if (b.Wins[0] >= _need || b.Wins[1] >= _need || b.Idle >= Duel.IdleRounds)
                {
                    // Матч вирішено. Троє сонних раундів поспіль теж вирішують: хто вів — той і далі, нічия — жереб
                    // (інакше одна покинута пара тримала б увесь стіл).
                    var side = b.Wins[0] != b.Wins[1] ? (b.Wins[0] > b.Wins[1] ? 0 : 1) : Ctx.Rng.Next(2);
                    Close(_cur!, b.Seats[side], b.Idle >= Duel.IdleRounds && b.Wins[side] < _need);
                    Proceed(now);
                }
                else b.Next(now);
                return Kit.Mark(TickResult.Both, now);
        }
        return Kit.PingTick(b.Phase, now);
    }

    void Close(Match m, int winner, bool walk)
    {
        m.Done = true;
        m.Walk = walk;
        m.Winner = winner;
        m.SA = Bout.Wins[0];
        m.SB = Bout.Wins[1];
    }

    /// <summary>Хтось устав: його матч — суперникові, його місце далі в сітці — прохід без бою. Лишився один — усе.</summary>
    public override void OnLeave(int seat)
    {
        if (seat is < 0 or >= Seats) return;
        var others = Enumerable.Range(0, Seats).Where(s => s != seat && Ctx.Seated(s) && !_gone[s]).ToArray();
        _gone[seat] = true;
        if (_over) return;
        if (others.Length <= 1)
        {
            _over = true;
            Bout.Finish();
            Ctx.Finish(others, $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, турнір не дограли");
            return;
        }
        Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} іде з турніру — суперник проходить без бою");
        if (_cur is { } m && (m.A == seat || m.B == seat))
        {
            Close(m, m.A == seat ? m.B!.Value : m.A!.Value, true);
            Proceed(Ctx.Clock.UtcNow);
        }
        _dirty = true;
    }

    public override object? Frame() => Wire(false);

    public override object View(int? seat) => Wire(true);

    object Wire(bool full)
    {
        var o = new Dictionary<string, object?>();
        Bout.Fill(o, Ctx.Clock.UtcNow);
        var pair = _cur is null ? null : new[] { Bout.Seats[0], Bout.Seats[1] };
        Kit.Fill(o, full, pair ?? []);
        o["pair"] = pair;
        o["need"] = _need;
        o["champ"] = _champ;
        // Сітка: кола → матчі [A, B, переможець, рахунок A, рахунок B, 1 — без бою].
        var cup = new object?[_rounds.Count][];
        for (var r = 0; r < _rounds.Count; r++)
        {
            var list = _rounds[r];
            cup[r] = new object?[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                var m = list[i];
                cup[r][i] = new object?[] { m.A, m.B, m.Winner, m.SA, m.SB, m.Walk ? 1 : 0 };
            }
        }
        o["cup"] = cup;
        return o;
    }
}
