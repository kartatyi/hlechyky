using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>Вимикач і числа Горохової дошки (appsettings.json → «Plinko», наживо). docs/games/specs/plinko.md §5.</summary>
public sealed class PlinkoOptions
{
    /// <summary>false — «дошка на перерві»: кидок відмовляє, у /api/me plinko=false, лобі ховає плитку.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Ставки за горошину. Порожньо — типові <see cref="DefaultBets"/>.</summary>
    public int[]? Bets { get; set; }
    /// <summary>Стеля ставки за горошину (0 — без стелі).</summary>
    public int MaxBet { get; set; }
    /// <summary>Скільки горошин найбільше за один кидок (1…<see cref="BallsCap"/>).</summary>
    public int MaxBalls { get; set; } = BallsCap;

    public static readonly int[] DefaultBets = [10, 20, 50, 100, 200, 500];
    /// <summary>Найменша ставка; і всі ставки кратні 10 — тоді ставка × множник (десяті) завжди цілий черепок.</summary>
    public const int MinBet = 10;
    public const int BallsCap = 10;

    public int Balls => Math.Clamp(MaxBalls, 1, BallsCap);

    /// <summary>Ставки, які справді можна поставити: кратні 10, не менше <see cref="MinBet"/>, не вище <see cref="MaxBet"/>, і щоб
    /// найбільший виграш кидка (ставка × ×1000 × 10 горошин) влазив у гаманець.</summary>
    public int[] AllowedBets()
    {
        var top = int.MaxValue / ((long)PlinkoCore.TopX * BallsCap / 10);
        bool Fits(int x) => x >= MinBet && x % 10 == 0 && x <= top && (MaxBet <= 0 || x <= MaxBet);
        var src = Bets is { Length: > 0 } b ? b : DefaultBets;
        var ok = src.Where(Fits).Distinct().Order().ToArray();
        return ok.Length > 0 ? ok : [.. DefaultBets.Where(Fits)];
    }
}

/// <summary>Що гра бере з контейнера: касу автоматів (журнал, відновлення, «Заноси тижня») і свої налаштування.</summary>
public sealed class PlinkoDesk(SlotsBank bank, Func<PlinkoOptions> options)
{
    public SlotsBank Bank { get; } = bank;
    public PlinkoOptions O => options();
}

/// <summary>Один кидок (до 10 горошин з одного seed): що поставлено, на якій дошці, відбиток, seed і горошини.</summary>
public sealed class PlinkoDrop
{
    public int Seq { get; set; }
    public int Bet { get; set; }
    public int Rows { get; set; }
    public string Risk { get; set; } = PlinkoCore.Low;
    public string Hash { get; set; } = "";
    public string Seed { get; set; } = "";
    public List<PlinkoBall> Balls { get; set; } = [];
    public int Win { get; set; }
}

/// <summary>Дошка одного гравця (Persistent: живе між заходами).</summary>
public sealed class PlinkoState
{
    /// <summary>Номер останнього кидка — клієнт програє кидки з більшим номером, ніж бачив.</summary>
    public int Seq { get; set; }
    public int Bet { get; set; }
    public int Rows { get; set; } = 12;
    public string Risk { get; set; } = PlinkoCore.Low;
    /// <summary>Скільки горошин за кидок обрано востаннє.</summary>
    public int Balls { get; set; } = 1;
    /// <summary>Останні кидки, свіжий — перший (до <see cref="Plinko.RecentLen"/>).</summary>
    public List<PlinkoDrop> Recent { get; set; } = [];
    /// <summary>Найбільший множник горошини (десяті) — за весь час і за день (для Ctx.Score: таблиця тримає найкраще за день).</summary>
    public int BestX { get; set; }
    public string Day { get; set; } = "";
    public int DayBestX { get; set; }
    /// <summary>Скільки горошин кинуто, поставлено й виграно за весь час.</summary>
    public long Drops { get; set; }
    public long Staked { get; set; }
    public long Won { get; set; }
    /// <summary>Ачівки, про які вже попросили.</summary>
    public List<string> Ach { get; set; } = [];

    /// <summary>Епоха процесу (нова на кожен Start і Load) — у ключах леджера, як у слотах: відкочений стан не повторить ключ.</summary>
    [JsonIgnore] public string Epoch { get; set; } = "";
    /// <summary>Seed наступного кидка — таємниця; у виді лише <see cref="NextHash"/>. Новий на кожен Start і Load.</summary>
    [JsonIgnore] public string NextSeed { get; set; } = "";
    [JsonIgnore] public string NextHash { get; set; } = "";
    /// <summary>Гаманець на мить останньої дії (у вид, щоб не ходити в базу з View).</summary>
    [JsonIgnore] public int Wallet { get; set; }
}

/// <summary>
/// «Горохова дошка» (плінко, docs/games/specs/plinko.md): соло, приватна, Persistent. Глек сипле горох на дошку з
/// кілочками — кожна горошина падає в глечик з множником. Кидок (1…10 горошин) сервер рахує цілком в одному
/// <see cref="Act"/>: шляхи з seed, чий відбиток людина бачила до кидка (<see cref="PlinkoCore"/>), ставка, виграш;
/// клієнт лише програє, як горошини котяться. Гроші — через касу автоматів <see cref="SlotsBank"/> у тому самому порядку,
/// що й оберт слота (журнал наперед → списання <c>{ref}:bet</c> → виграш <c>{ref}:win</c>), але з причинами
/// <c>plinko-bet</c> / <c>plinko-win</c>; у Скарбничку дошка не кладе. Великі горошини (≥ ×20) — у «Заносах тижня».
/// </summary>
public sealed class Plinko : Game
{
    public const string ActDrop = "drop";
    /// <summary>Голова причин у леджері: <c>plinko-bet:plinko</c>, <c>plinko-win:plinko</c>.</summary>
    public const string Kind = "plinko";
    public const int RecentLen = 6;
    /// <summary>Горошина від ×20 — у «Заноси тижня» (каса сама відсіює), від ×100 — ачівка й рядок у Балачки.</summary>
    public const int FeedX = 200, BigX = 1000;

    public const string OffText = "Горохова дошка на перерві — Глек збирає розсипаний горох";
    public const string ClosedText = "Дошка зачинена — каса не відповідає";
    public const string StaleText = "Глек уже набрав нову жменю — кидай ще раз";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "plinko", "Горохова дошка", "Горохову дошку", GameGroup.Solo, 1, 1, Start: StartMode.Immediate,
        Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Плінко по-нашому: Глек сипле горох на дошку з кілочками — куди відскочить, у той глечик і впаде. Край — до ×1000");

    PlinkoDesk? _desk;
    PlinkoState S = new();

    public PlinkoState State => S;
    PlinkoOptions O => _desk?.O ?? new PlinkoOptions();

    public override string SeatName(int seat) => "гравець";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        try { _desk = Ctx?.Services?.GetService<PlinkoDesk>(); }
        catch { _desk = null; }
    }

    /// <summary>Відкриття дошки (і «Ще раз»): налаштування, історія й рекорди лишаються; епоха й seed — нові.</summary>
    public override void Start()
    {
        S.Epoch = NewEpoch();
        NextSeed();
        Refresh();
    }

    public override object? Frame() => null;

    public override string? Save() => JsonSerializer.Serialize(S, Json);

    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<PlinkoState>(json, Json) ?? new PlinkoState();
        s.Recent ??= [];
        s.Ach ??= [];
        if (!PlinkoCore.IsRows(s.Rows)) s.Rows = 12;
        if (!PlinkoCore.IsRisk(s.Risk)) s.Risk = PlinkoCore.Low;
        s.Balls = Math.Clamp(s.Balls, 1, PlinkoOptions.BallsCap);
        S = s;
        S.Epoch = NewEpoch();
        NextSeed();
        Refresh();
    }

    void Refresh()
    {
        if (_desk is not null && Ctx?.NickOf(0) is { } nick) S.Wallet = _desk.Bank.Balance(nick);
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        var o = O;
        var bets = o.AllowedBets();
        return new
        {
            on = o.Enabled && _desk is not null,
            bets,
            bet = S.Bet > 0 && bets.Contains(S.Bet) ? S.Bet : bets.FirstOrDefault(),
            rows = S.Rows,
            risk = S.Risk,
            balls = Math.Min(S.Balls, o.Balls),
            maxBalls = o.Balls,
            balance = seat is null ? 0 : S.Wallet,
            seq = S.Seq,
            // відбиток наступного кидка: seed за ним з'явиться в recent, коли кинеш
            hash = seat is null ? null : S.NextHash,
            recent = S.Recent.Select(d => new
            {
                seq = d.Seq, bet = d.Bet, rows = d.Rows, risk = d.Risk, hash = d.Hash, seed = d.Seed, win = d.Win,
                balls = d.Balls.Select(b => new { p = b.P, s = b.S, x = b.X, win = b.Win }),
            }),
            tables = PlinkoCore.All(),   // множники в десятих, [ризик][ряди − 8][глечик]
            rtp = PlinkoCore.Rtp98,
            best = S.BestX / 10.0,
            dayBest = S.DayBestX / 10.0,
            drops = S.Drops,
            staked = S.Staked,
            won = S.Won,
        };
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != ActDrop) return ActResult.Fail("Тут так не ходять");
        if (_desk is null) return ActResult.Fail(ClosedText);
        if (Ctx.NickOf(seat) is not { } nick) return ActResult.Fail("Тут так не ходять");
        return Drop(seat, nick, payload);
    }

    string Ref(int seq) => $"plinko:{Ctx.RoomId}-{S.Epoch}:{seq}";

    /// <summary>
    /// Кидок: <c>{ bet, n, rows, risk, hash? }</c>. <c>hash</c> — відбиток, який людина бачила: не той (кинула з іншої
    /// вкладки, чи вид ще не дійшов) — відмова, щоб «чесно наперед» стосувалось саме цього кидка. Гроші — як оберт слота:
    /// журнал наперед (якщо є що платити), списання, виграш; збій посередині доплатить підмітання каси тими самими ключами.
    /// </summary>
    ActResult Drop(int seat, string nick, JsonElement p)
    {
        var desk = _desk!;
        var bank = desk.Bank;
        var o = desk.O;
        if (!o.Enabled) return ActResult.Fail(OffText);
        var bets = o.AllowedBets();
        if (Int(p, "bet") is not { } bet || !bets.Contains(bet))
            return ActResult.Fail($"Така ставка не йде: {string.Join(", ", bets)}");
        var rows = Int(p, "rows") ?? S.Rows;
        if (!PlinkoCore.IsRows(rows)) return ActResult.Fail($"Рядів — від {PlinkoCore.MinRows} до {PlinkoCore.MaxRows}");
        var risk = Str(p, "risk") ?? S.Risk;
        if (!PlinkoCore.IsRisk(risk)) return ActResult.Fail("Ризик — низький, середній чи високий");
        var n = Int(p, "n") ?? 1;
        if (n < 1 || n > o.Balls) return ActResult.Fail(o.Balls == 1 ? "За раз — одна горошина" : $"За раз — від 1 до {o.Balls} горошин");
        if (Str(p, "hash") is { } seen && !string.Equals(seen, S.NextHash, StringComparison.OrdinalIgnoreCase))
            return ActResult.Fail(StaleText);
        var cost = bet * n;
        var wallet = bank.Balance(nick);
        S.Wallet = wallet;
        if (wallet < cost) return ActResult.Fail(Short(wallet, cost));

        if (string.IsNullOrEmpty(S.NextSeed)) NextSeed();
        var seed = S.NextSeed;
        var hash = S.NextHash;
        var balls = PlinkoCore.Drop(seed, n, rows, risk, bet);
        var win = (int)Math.Min(int.MaxValue, balls.Sum(b => (long)b.Win));
        var seq = S.Seq + 1;
        var reference = Ref(seq);
        var pending = new SlotPending(reference, nick, Info.Id, cost, win, 0, Ctx.Clock.UtcNow, Kind);
        var journal = win > 0;
        if (journal && !bank.Open(pending)) return ActResult.Fail(ClosedText);
        var taken = bank.Charge(nick, cost, $"{Kind}-bet:{Info.Id}", reference + ":bet");
        if (taken is null)
        {
            // Списання могло й пройти: запис журналу лишаємо — підмітання звірить із леджером і доплатить. Ключ і seed
            // цього кидка більше не беремо.
            S.Seq = seq;
            NextSeed();
            return ActResult.Fail(ClosedText);
        }
        if (taken == false)
        {
            if (journal) bank.Forget(reference);
            S.Wallet = bank.Balance(nick);
            return ActResult.Fail(Short(S.Wallet, cost));
        }
        if (journal) bank.Close(pending);   // не вийшло — запис лишився, підмітання доплатить тими самими ключами

        S.Seq = seq;
        S.Bet = bet;
        S.Rows = rows;
        S.Risk = risk;
        S.Balls = n;
        S.Recent.Insert(0, new PlinkoDrop { Seq = seq, Bet = bet, Rows = rows, Risk = risk, Hash = hash, Seed = seed, Balls = [.. balls], Win = win });
        if (S.Recent.Count > RecentLen) S.Recent.RemoveRange(RecentLen, S.Recent.Count - RecentLen);
        S.Drops += n;
        S.Staked += cost;
        S.Won += win;
        S.Wallet = (int)Math.Min(int.MaxValue, (long)wallet - cost + win);
        NextSeed();

        var top = balls.Max(b => b.X);
        if (top > S.BestX) S.BestX = top;
        var day = Days.Today(Ctx.Clock).ToString();
        if (day != S.Day) { S.Day = day; S.DayBestX = 0; }
        if (top > S.DayBestX)
        {
            S.DayBestX = top;
            Ctx.Score(seat, top / 10.0);
        }
        if (top >= BigX) Ach(seat, "plinko-100");
        if (rows == PlinkoCore.MaxRows && balls.Any(b => b.S == 0 || b.S == rows)) Ach(seat, "plinko-edge");
        foreach (var b in balls)
            if (b.X >= FeedX) bank.Brag(nick, Info.Id, Info.Title, bet, b.Win, 0, m => PlinkoLines.Big(nick, m, b.Win));
        return ActResult.Done;
    }

    static string Short(int wallet, int cost) => $"Бракує черепків: у гаманці {Math.Max(0, wallet)}, а кидок коштує {cost}";

    void Ach(int seat, string key)
    {
        if (S.Ach.Contains(key)) return;
        S.Ach.Add(key);
        Ctx.Award(seat, 0, "ach:" + key);
    }

    /// <summary>Сід кімнати в тестах (відтворювано), криптографічний генератор у проді; гра без каркаса (Load у тестах) — теж він.</summary>
    Random? SeededRng => Ctx is { Seeded: true } c ? c.Rng : null;

    void NextSeed()
    {
        S.NextSeed = PlinkoCore.NewSeed(SeededRng);
        S.NextHash = PlinkoCore.Hash(S.NextSeed);
    }

    string NewEpoch() => SeededRng is { } rng
        ? ((uint)rng.Next(int.MinValue, int.MaxValue)).ToString("x8")
        : RandomNumberGenerator.GetHexString(8, lowercase: true);

    static int? Int(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Репліки Глека в Балачки про горох.</summary>
public static class PlinkoLines
{
    public static string Big(string nick, double mult, int win) =>
        $"🟢 {nick} — ×{Math.Floor(mult)} на Гороховій дошці: +{win} {SlotLines.Shards(win)}. Глек лізе під лаву по розсипаний горох";
}

/// <summary>Підключення Горохової дошки в <see cref="GamesSetup"/>: налаштування Plinko:* і каса автоматів.</summary>
public static class PlinkoSetup
{
    public static IServiceCollection AddPlinko(IServiceCollection services)
    {
        services.AddOptions<PlinkoOptions>().BindConfiguration("Plinko");
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<PlinkoOptions>>();
            return new PlinkoDesk(sp.GetRequiredService<SlotsBank>(), () => opts.CurrentValue);
        });
        return services;
    }
}
