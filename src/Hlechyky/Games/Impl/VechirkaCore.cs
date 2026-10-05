using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Власний ГВЧ вечірки (SplitMix64): стан — одне число, живе в Save, тож відновлена вечірка детермінована.</summary>
public sealed class VechirkaRng(ulong state)
{
    public ulong State = state;

    public ulong NextU()
    {
        var z = State += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Ціле в [0, max).</summary>
    public int Next(int max) => max <= 1 ? 0 : (int)(NextU() % (ulong)max);
    public int Next(int min, int maxExcl) => min + Next(maxExcl - min);
    public double NextDouble() => (NextU() >> 11) * (1.0 / (1UL << 53));

    public int Weighted(IReadOnlyList<int> w)
    {
        var sum = w.Sum();
        if (sum <= 0) return Next(w.Count);
        var x = Next(sum);
        for (var i = 0; i < w.Count; i++) { if (x < w[i]) return i; x -= w[i]; }
        return w.Count - 1;
    }
}

/// <summary>Особисті лічильники вечора: номінації (§9) і ачівки (§10).</summary>
public sealed class VechirkaStats
{
    public int Earned { get; set; }
    public int Steps { get; set; }
    public int Bully { get; set; }
    public int Traps { get; set; }
    public int Shop { get; set; }
    public int Chests { get; set; }
    public int Pans { get; set; }
    public int Banks { get; set; }
    public int Ferries { get; set; }
    public int Bought { get; set; }
    /// <summary>Був останнім після половини кіл (для «З печі на покуть»).</summary>
    public bool LastAtHalf { get; set; }
}

/// <summary>Гравець вечірки: місце за ніком, не за кріслом (§1.2). Індекс у <see cref="VechirkaState.P"/> — назавжди.</summary>
public sealed class VechirkaPlayer
{
    public string? Nick { get; set; }
    public bool Bot { get; set; }
    public string Name { get; set; } = "";
    public bool Away { get; set; }
    public bool Auto { get; set; }
    public string Pos { get; set; } = "";
    public int Coins { get; set; }
    public int Gleks { get; set; }
    public List<string> Items { get; set; } = [];
    public bool Bump { get; set; }
    public int MgWins { get; set; }
    public int Color { get; set; }
    /// <summary>Скільки фаз поспіль прострочено (дві — «задрімав»).</summary>
    public int Misses { get; set; }
    /// <summary>Уже ходив цього вечора (підказка першого ходу).</summary>
    public bool Moved { get; set; }
    public VechirkaStats S { get; set; } = new();
    [JsonIgnore] public bool Charm => Items.Contains("charm");
    /// <summary>За нього думає машина: бот лобі, людина відпала або задрімала.</summary>
    [JsonIgnore] public bool Machine => Bot || Away || Auto;
}

public sealed record VechirkaOpt(string K, string Label, bool Ok = true, int? Price = null);

/// <summary>Рішення посеред ходу (§2.3). Then — куди автомат іде після відповіді.</summary>
public sealed class VechirkaPrompt
{
    public int Who { get; set; }
    public string Kind { get; set; } = "";
    public string? Node { get; set; }
    public List<VechirkaOpt> Options { get; set; } = [];
    public int Default { get; set; }
    public string Then { get; set; } = "";
    /// <summary>Для discard: новий предмет (останній варіант).</summary>
    public string? NewItem { get; set; }
}

/// <summary>Стан ходу: кроки, кубики, що вже вжито й куплено.</summary>
public sealed class VechirkaTurn
{
    public int Steps { get; set; }
    public int[] Dice { get; set; } = [];
    public bool Rolled { get; set; }
    public bool UsedItem { get; set; }
    public bool Horse { get; set; }
    public bool Bought { get; set; }
    public bool ShopSkip { get; set; }
    public bool Broke { get; set; }
    public bool Wind { get; set; }
    /// <summary>Розвилку вже вирішено: наступний крок — сюди.</summary>
    public string? Hop { get; set; }
    /// <summary>Чому рух зупинився: fork, stand, shop, land.</summary>
    public string Stop { get; set; } = "";
    /// <summary>Крамниця, що вже питала на цій клітинці (щоб не питати двічі на зупинці).</summary>
    public string? Asked { get; set; }
}

public sealed class VechirkaAim
{
    public string Item { get; set; } = "";
    public List<int>? Targets { get; set; }
    public List<string>? Nodes { get; set; }
    public int[]? Range { get; set; }
}

public sealed class VechirkaPick
{
    public int? Chooser { get; set; }
    public List<string> Options { get; set; } = [];
    public string? Chosen { get; set; }
    public bool Duel { get; set; }
}

public sealed class VechirkaMgState
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Howto { get; set; } = "";
    public bool Repeat { get; set; }
    public bool X2 { get; set; }
    public int[]? Duel { get; set; }
    public int Stake { get; set; }
    public bool Honor { get; set; }
    public Dictionary<int, int> Bets { get; set; } = [];
    public List<int> Ready { get; set; } = [];
    public int[] Seats { get; set; } = [];
    public bool Running { get; set; }
    public List<VechirkaMgLine>? Results { get; set; }
    public string? How { get; set; }
}

public sealed record VechirkaMgLine(int I, int Place, int Coins);

public sealed class VechirkaLate
{
    public List<int> Choosers { get; set; } = [];
    public Dictionary<int, string> Chosen { get; set; } = [];
}

public sealed record VechirkaBonusWin(string Key, string Title, int[] Winners);

public sealed class VechirkaFinal
{
    public int Step { get; set; }
    public List<VechirkaBonusWin> Bonuses { get; set; } = [];
    public List<int[]> Ranking { get; set; } = [];
}

public sealed record VechirkaGateSt(string Node, int Owner);

public sealed class VechirkaAnim
{
    public int Seq { get; set; }
    public string Kind { get; set; } = "";
    public int? Who { get; set; }
    public List<string>? Path { get; set; }
    public int[]? Dice { get; set; }
    public string? Key { get; set; }
    public int? To { get; set; }
    public DateTimeOffset At { get; set; }
    public int Ms { get; set; }
}

public sealed record VechirkaFx(int Seq, string Kind, int Who, int D = 0, string? K = null);

/// <summary>Увесь стан вечірки — один серіалізований об'єкт (Save/Load §17).</summary>
public sealed class VechirkaState
{
    public int V { get; set; } = 1;
    public string Map { get; set; } = "selo";
    public int MapV { get; set; } = 1;
    public int Len { get; set; } = 45;
    public List<string> Minis { get; set; } = ["move", "brain", "tap"];
    public LiveBots.Level Level { get; set; } = LiveBots.Level.Normal;
    public ulong Rng { get; set; }
    public int Round { get; set; }
    public int Rounds { get; set; }
    public string Phase { get; set; } = "lobby";
    public int TurnIdx { get; set; }
    public int? Cur { get; set; }
    public int[] Order { get; set; } = [];
    public List<VechirkaPlayer> P { get; set; } = [];
    public string Stand { get; set; } = "";
    public int SaleUntil { get; set; }
    public int Bank { get; set; }
    public List<VechirkaGateSt> Gates { get; set; } = [];
    public Dictionary<string, string[]> Shops { get; set; } = [];
    public string[] BonusKeys { get; set; } = [];
    /// <summary>Зіграні міні-ігри: id → коло (останнього разу) і скільки разів.</summary>
    public Dictionary<string, int> MgLast { get; set; } = [];
    public Dictionary<string, int> MgCount { get; set; } = [];
    public string? LastDuelGame { get; set; }
    public bool DuelDone { get; set; }
    public bool Late { get; set; }
    public bool NoFun { get; set; }
    public string? LastEvent { get; set; }
    public VechirkaTurn T { get; set; } = new();
    public VechirkaPrompt? Pr { get; set; }
    public VechirkaAim? Am { get; set; }
    public VechirkaPick? Pk { get; set; }
    public VechirkaMgState? M { get; set; }
    public VechirkaLate? L { get; set; }
    public VechirkaFinal? F { get; set; }
    public List<VechirkaPrompt> Queue { get; set; } = [];
    public int Seq { get; set; }
    public VechirkaAnim? Anim { get; set; }
    public List<VechirkaFx> Fx { get; set; } = [];
    public List<string> Log { get; set; } = [];
    public string? Then { get; set; }
    public DateTimeOffset? Busy { get; set; }
    public DateTimeOffset? Until { get; set; }
    public int Total { get; set; }
    public DateTimeOffset? ThinkAt { get; set; }
    public string? Paused { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public bool HalfMarked { get; set; }
    public int? Leader { get; set; }
    public bool Done { get; set; }
}

/// <summary>Подія назовні (для кімнати): рядок Журналу вечірки, репліка Глека за ключем пулу, кінець.</summary>
public sealed record VechirkaOut(string Kind, string Key, IReadOnlyDictionary<string, string>? Args = null);

/// <summary>Мізки за машину (бот лобі, відпалий, задрімалий): S1.2 — типові рішення, S1.3 — <c>VechirkaBot</c>.</summary>
public interface IVechirkaBrain
{
    /// <summary>Зробити хід за <paramref name="i"/> через <see cref="VechirkaCore.Act"/>; нічого не зробив — ядро візьме типове.</summary>
    void Think(VechirkaCore core, int i);
}

/// <summary>
/// Ядро вечірки (§1–§9) без кімнати: автомат фаз, ходи, клітинки, предмети, події, міні-гра через <see cref="IMgRunner"/>,
/// фінал. Час — лише переданий ззовні (<see cref="Advance"/>), рандом — власний ГВЧ. Назовні — через <see cref="Out"/>.
/// </summary>
public sealed partial class VechirkaCore
{
    public VechirkaState S { get; private set; }
    public VechirkaMap Map { get; }
    public IMgRunner Mg { get; set; }
    public IVechirkaBrain? Brain { get; set; }
    public IReadOnlyList<VechirkaPoolEntry> Pool { get; set; }
    public List<VechirkaOut> Out { get; } = [];
    public DateTimeOffset Now { get; private set; }
    VechirkaRng R;
    bool _machine;

    public VechirkaCore(VechirkaMap map, VechirkaState state, IMgRunner mg, IReadOnlyList<VechirkaPoolEntry> pool)
    {
        Map = map;
        S = state;
        Mg = mg;
        Pool = pool;
        R = new VechirkaRng(state.Rng);
    }

    public int N => S.P.Count;
    /// <summary>Рандом вечірки для мізків ботів (той самий ГВЧ — детермінізм за сідом).</summary>
    public int Rand(int max) => R.Next(max);
    public VechirkaPlayer this[int i] => S.P[i];
    public int Price => S.SaleUntil >= S.Round && S.SaleUntil > 0 ? VechirkaRules.SalePrice : VechirkaRules.GlekPrice;

    // ---------- старт ----------

    /// <summary>Нова вечірка: P уже заповнено (люди, потім боти лобі). Сід — з ГВЧ кімнати.</summary>
    public void Start(ulong seed, DateTimeOffset now)
    {
        Now = now;
        R = new VechirkaRng(seed);
        S.Rounds = VechirkaRules.Rounds(S.Len, N);
        for (var i = 0; i < N; i++)
        {
            var p = S.P[i];
            p.Pos = Map.Start; p.Coins = VechirkaRules.StartCoins; p.Gleks = 0; p.Items.Clear(); p.Bump = false;
            p.MgWins = 0; p.Color = i; p.S = new(); p.Moved = false; p.Misses = 0; p.Auto = false;
        }
        // Номінації таємні від старту; на великому столі «Невдаха вечора» — завжди (утіха, §9).
        var keys = VechirkaRules.BonusList.Select(b => b.Key).ToList();
        var pickB = new List<string>();
        if (N >= 6) { pickB.Add("traps"); keys.Remove("traps"); }
        while (pickB.Count < VechirkaRules.Bonuses) { var k = R.Next(keys.Count); pickB.Add(keys[k]); keys.RemoveAt(k); }
        S.BonusKeys = [.. pickB];
        var far = Map.Stands.Where(s => (Map.Forward(Map.Start, s, false) ?? 99) >= VechirkaRules.StandMinDist).ToList();
        if (far.Count == 0) far = [.. Map.Stands];
        S.Stand = far[R.Next(far.Count)];
        S.Round = 0;
        Say("intro", ("n", N.ToString()));
        Phase("intro", VechirkaRules.IntroMs);
        S.Then = "order";
        Save0();
    }

    void Save0() => S.Rng = R.State;

    // ---------- автомат ----------

    /// <summary>Крутить автомат, доки не впреться в очікування (людина, busy, таймер). ≤ 64 кроки за виклик.</summary>
    public void Advance(DateTimeOffset now)
    {
        Now = now;
        if (S.Done) return;
        if (S.Paused == "host" && S.PausedAt is { } pa && now - pa > TimeSpan.FromMilliseconds(VechirkaRules.PauseMaxMs))
            SetPause(null);
        for (var guard = 0; guard < 64; guard++)
        {
            if (S.Done) break;
            if (S.Phase == "mg") { if (!MgStep()) break; continue; }
            if (S.Paused is not null) break;
            if (S.Busy is { } b)
            {
                if (now < b) break;
                S.Busy = null;
                var then = S.Then; S.Then = null;
                Run(then);
                continue;
            }
            if (S.ThinkAt is { } t && now >= t)
            {
                S.ThinkAt = null;
                MachineAct();
                continue;
            }
            if (S.Until is { } u && now >= u)
            {
                S.Until = null;
                Expire();
                continue;
            }
            if (S.Then is { } th && S.Until is null && S.Busy is null && !Waiting)
            {
                S.Then = null;
                Run(th);
                continue;
            }
            break;
        }
        Save0();
    }

    /// <summary>Фаза чекає на рішення (людини чи машини) — Then не запускаємо.</summary>
    bool Waiting => S.Phase is "turn" or "aim" or "prompt" or "pick" or "card" or "late" && S.Then is null;

    void Phase(string phase, int ms)
    {
        S.Phase = phase;
        S.Total = ms;
        S.Until = ms > 0 ? Now.AddMilliseconds(ms) : null;
        S.Busy = null;
        ScheduleMachine();
    }

    void BusyFor(int ms, string then)
    {
        S.Busy = Now.AddMilliseconds(ms);
        S.Then = then;
    }

    /// <summary>Таймер фази минув — типове рішення (§1.1, §2.3).</summary>
    void Expire()
    {
        switch (S.Phase)
        {
            case "intro" or "order":
                var th = S.Then ?? (S.Phase == "intro" ? "order" : "round");
                S.Then = null;
                Run(th);
                break;
            case "turn":
                Miss(S.Cur);
                DoRoll(null);
                break;
            case "aim":
                Miss(S.Cur);
                S.Am = null;
                Phase("turn", VechirkaRules.TurnMs);
                break;
            case "prompt":
                if (S.Pr is { } pr) { Miss(pr.Who); Answer(pr, pr.Default); }
                break;
            case "pick":
                if (S.Pk is { Chosen: null } pk) Choose(pk, R.Next(pk.Options.Count));
                break;
            case "card": BeginMg(); break;
            case "late":
                if (S.L is { } l) foreach (var c in l.Choosers) l.Chosen.TryAdd(c, "coins");
                FinishLate();
                break;
            case "results": AfterResults(); break;
            case "final": FinalStep(); break;
        }
    }

    void Miss(int? i)
    {
        if (i is not { } k || k < 0 || k >= N) return;
        var p = S.P[k];
        if (p.Machine) return;
        if (++p.Misses >= 2 && !p.Auto)
        {
            p.Auto = true;
            Say("afk", ("nick", p.Name));
            Line($"💤 {p.Name} задрімав — ходить Глек");
        }
    }

    void Run(string? then)
    {
        switch (then)
        {
            case null: break;
            case "order": DoOrder(); break;
            case "round": NextRound(); break;
            case "turn": BeginTurn(); break;
            case "move": Move(); break;
            case "stop": AtStop(); break;
            case "land": Land(); break;
            case "endTurn": EndTurn(); break;
            case "nextTurn": NextTurn(); break;
            case "card": BeginCard(); break;
            case "results": AfterResults(); break;
            case "late": FinishLate(); break;
            case "final": FinalStep(); break;
            case "queue": NextQueued(); break;
            default: throw new InvalidOperationException("невідомий крок " + then);
        }
    }

    void DoOrder()
    {
        var rolls = Enumerable.Range(0, N).Select(_ => R.Next(1, 7) + R.Next(1, 7)).ToArray();
        var tie = Enumerable.Range(0, N).Select(_ => R.Next(1 << 20)).ToArray();
        S.Order = [.. Enumerable.Range(0, N).OrderByDescending(i => rolls[i]).ThenBy(i => tie[i])];
        Anim("order", null, dice: rolls);
        Say("order", ("nick", S.P[S.Order[0]].Name));
        Line("🎲 Порядок: " + string.Join(", ", S.Order.Select(i => S.P[i].Name)));
        Phase("order", VechirkaRules.OrderMs);
        S.Then = "round";
    }

    void NextRound()
    {
        S.Round++;
        if (S.Round > S.Rounds) { BeginFinal(); return; }
        S.DuelDone = false;
        if (S.SaleUntil > 0 && S.SaleUntil < S.Round) S.SaleUntil = 0;
        foreach (var shop in Map.Nodes.Where(n => n.Type == "shop"))
        {
            var pool = VechirkaRules.Buyable.ToList();
            var three = new string[3];
            for (var k = 0; k < 3; k++) { var x = R.Next(pool.Count); three[k] = pool[x]; pool.RemoveAt(x); }
            S.Shops[shop.Id] = three;
        }
        if (!S.HalfMarked && S.Round > (S.Rounds + 1) / 2)
        {
            // «З печі на покуть»: останній після половини кіл
            S.HalfMarked = true;
            foreach (var i in Lasts(1)) S.P[i].S.LastAtHalf = true;
        }
        S.TurnIdx = 0;
        if (S.Rounds >= 3 && S.Round == S.Rounds - 2) { BeginLate(); return; }
        BeginTurn();
    }

    // ---------- місця ----------

    /// <summary>Порівняння «краще»: глеки → шеляги → перемоги в міні-іграх.</summary>
    public long Rank(int i) => VechirkaRules.Score(S.P[i].Gleks, S.P[i].Coins, S.P[i].MgWins);

    public int PlaceOf(int i) => 1 + Enumerable.Range(0, N).Count(j => Rank(j) > Rank(i));

    /// <summary>
    /// Останні за місцем (менше глеків, далі шелягів; рівні — той, хто пізніше в порядку ходів): k штук.
    /// </summary>
    public List<int> Lasts(int k)
    {
        var ord = S.Order.Length == N ? S.Order : [.. Enumerable.Range(0, N)];
        return [.. Enumerable.Range(0, N)
            .OrderBy(i => S.P[i].Gleks).ThenBy(i => S.P[i].Coins).ThenByDescending(i => Array.IndexOf(ord, i))
            .Take(k)];
    }

    /// <summary>Нижня половина за місцем (N / 2 найнижчих).</summary>
    public HashSet<int> LowerHalf() => [.. Lasts(N / 2)];

    public int? LeaderIdx()
    {
        var best = Enumerable.Range(0, N).Max(Rank);
        var top = Enumerable.Range(0, N).Where(i => Rank(i) == best).ToList();
        return top.Count == 1 ? top[0] : null;
    }

    // ---------- журнал, репліки, анімації ----------

    public void Line(string text)
    {
        S.Log.Add(text);
        if (S.Log.Count > 12) S.Log.RemoveAt(0);
        Out.Add(new VechirkaOut("log", text));
    }

    public void Say(string pool, params (string K, string V)[] args) =>
        Out.Add(new VechirkaOut("say", pool, args.Length == 0 ? null : args.ToDictionary(a => a.K, a => a.V)));

    int Anim(string kind, int? who, List<string>? path = null, int[]? dice = null, string? key = null, int? to = null, int ms = 0)
    {
        S.Seq++;
        S.Anim = new VechirkaAnim { Seq = S.Seq, Kind = kind, Who = who, Path = path, Dice = dice, Key = key, To = to, At = Now, Ms = ms };
        S.Fx.RemoveAll(f => f.Seq < S.Seq - 6);
        return S.Seq;
    }

    void Fx(string kind, int who, int d = 0, string? k = null)
    {
        S.Fx.Add(new VechirkaFx(S.Seq, kind, who, d, k));
        while (S.Fx.Count > 8) S.Fx.RemoveAt(0);
    }

    // ---------- гроші ----------

    /// <summary>Надходження (рахується в «Багатія»), у Пізній вечір множення — на боці виклику.</summary>
    void Gain(int i, int d, string why = "coins")
    {
        if (d <= 0) return;
        S.P[i].Coins += d;
        S.P[i].S.Earned += d;
        Fx(why, i, d);
    }

    /// <summary>Скільки є — стільки й віддає. Повертає, скільки справді знято.</summary>
    int Take(int i, int d)
    {
        var x = Math.Min(Math.Max(0, d), S.P[i].Coins);
        S.P[i].Coins -= x;
        if (x > 0) Fx("coins", i, -x);
        return x;
    }

    void ToBank(int i, int d) => S.Bank += Take(i, d);

    void Transfer(int from, int to, int d)
    {
        var x = Take(from, d);
        Gain(to, x);
    }

    // ---------- машини ----------

    /// <summary>Хто зараз має діяти: для думання машин і для «чи всі відповіли».</summary>
    public IEnumerable<int> Actors()
    {
        switch (S.Phase)
        {
            case "turn" or "aim": if (S.Cur is { } c) yield return c; break;
            case "prompt": if (S.Pr is { } pr) yield return pr.Who; break;
            case "pick": if (S.Pk is { Chosen: null, Chooser: { } ch }) yield return ch; break;
            case "late":
                if (S.L is { } l) foreach (var x in l.Choosers) if (!l.Chosen.ContainsKey(x)) yield return x;
                break;
        }
    }

    /// <summary>Затримка машини — з хешу (seq, i), а не з ГВЧ: таймери не зсувають рандом гри (§1.2).</summary>
    int Delay(int i)
    {
        var p = S.P[i];
        var h = (uint)((S.Seq * 7919 + i * 104729 + S.Round * 31) & 0x7fffffff);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return p.Bot ? VechirkaRules.BotMinMs + (int)(h % (VechirkaRules.BotMaxMs - VechirkaRules.BotMinMs)) : VechirkaRules.AwayMs;
    }

    void ScheduleMachine()
    {
        S.ThinkAt = null;
        foreach (var i in Actors())
            if (S.P[i].Machine)
            {
                var at = Now.AddMilliseconds(Delay(i));
                if (S.ThinkAt is null || at < S.ThinkAt) S.ThinkAt = at;
            }
    }

    void MachineAct()
    {
        var phase = S.Phase; var seq = S.Seq;
        foreach (var i in Actors().ToList())
        {
            if (!S.P[i].Machine) continue;
            _machine = true;
            try { Brain?.Think(this, i); } catch (GameError) { }
            finally { _machine = false; }
            if (S.Phase != phase || S.Seq != seq) return;
            // мізки не зробили нічого — типове рішення
            if (Actors().Contains(i)) DefaultFor(i);
            if (S.Phase != phase) return;
        }
        if (S.ThinkAt is null) ScheduleMachine();
    }

    void DefaultFor(int i)
    {
        switch (S.Phase)
        {
            case "turn": DoRoll(null); break;
            case "aim": S.Am = null; Phase("turn", VechirkaRules.TurnMs); break;
            case "prompt": if (S.Pr is { } pr) Answer(pr, pr.Default); break;
            case "pick": if (S.Pk is { } pk) Choose(pk, R.Next(pk.Options.Count)); break;
            case "late":
                S.L!.Chosen.TryAdd(i, "coins");
                if (S.L.Choosers.All(S.L.Chosen.ContainsKey)) FinishLate();
                break;
        }
    }
}
