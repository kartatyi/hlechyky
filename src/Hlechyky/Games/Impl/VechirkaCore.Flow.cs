using System.Text.Json;

namespace Hlechyky.Games.Impl;

// Рішення, дуель, міні-гра, Пізній вечір, фінал, дії й Save/Load (§2.3, §8, §9, §14.2, §17).
public sealed partial class VechirkaCore
{
    // ---------- відповіді на prompt ----------

    void Answer(VechirkaPrompt pr, int o)
    {
        if (o < 0 || o >= pr.Options.Count) throw new GameError("Такого варіанта нема");
        var opt = pr.Options[o];
        if (!opt.Ok) throw new GameError("Цей варіант зараз не можна");
        var i = pr.Who;
        var p = S.P[i];
        S.Pr = null;
        var then = pr.Then;
        switch (pr.Kind)
        {
            case "fork" or "ferry":
            {
                var parts = opt.K.Split(':');
                var to = parts[1];
                if (parts[0] is "pay" or "key")
                {
                    var g = Map.GateOf(p.Pos, to)!;
                    if (parts[0] == "pay") ToBank(i, g.Cost); else p.Items.Remove("key");
                    p.S.Ferries++;
                    Say("ferry", ("nick", p.Name));
                }
                S.T.Hop = to;
                break;
            }
            case "stand":
                if (opt.K == "buy")
                {
                    var price = Price;
                    p.Coins -= price;
                    Fx("coins", i, -price);
                    p.Gleks++;
                    p.S.Bought++;
                    S.T.Bought = true;
                    Fx("glek", i, 1);
                    Anim("buy", i, key: S.Stand, ms: VechirkaRules.BuyMs);
                    Line($"🏺 {p.Name} купив золотий глек!");
                    Say("buy", ("nick", p.Name), ("n", p.Gleks.ToString()));
                    var was = S.Leader;
                    S.Leader = LeaderIdx();
                    if (S.Leader is { } ld && ld != was && S.P[ld].Gleks > 0) Say("lead", ("nick", S.P[ld].Name));
                    MoveStand();
                    S.Phase = "walk";
                    S.Until = null;
                    BusyFor(VechirkaRules.BuyMs, then);
                    return;
                }
                Say("noBuy", ("nick", p.Name));
                break;
            case "shop":
                if (opt.K == "skip") S.T.ShopSkip = true;
                else if (opt.K != "none")
                {
                    var it = VechirkaRules.Item[opt.K];
                    p.Coins -= it.Price;
                    Fx("coins", i, -it.Price);
                    p.S.Shop += it.Price;
                    p.Items.Add(opt.K);
                    Fx("item", i, k: opt.K);
                    if (R.Next(3) == 0) Say("shopBuy", ("nick", p.Name), ("item", it.Name));
                }
                break;
            case "discard":
            {
                // викинути обране; якщо це не новий — новий лягає в руку
                if (o < p.Items.Count) { p.Items.RemoveAt(o); p.Items.Add(pr.NewItem!); Fx("item", i, k: pr.NewItem); }
                break;
            }
            case "event":
                if (opt.K == "pay") ToBank(i, 3); else Bump(i, null);
                break;
            case "duelWho":
            {
                var foe = int.Parse(opt.K);
                var max = Math.Min(p.Coins, S.P[foe].Coins);
                if (max <= 0) { StartDuel(i, foe, 0); return; }
                var opts = VechirkaRules.Stakes.Select(s => new VechirkaOpt(s.ToString(), s == 0 ? "На честь" : $"{s} 🪙", s <= max)).ToList();
                var def = opts.FindLastIndex(x => x.Ok && int.Parse(x.K) <= 10);
                Ask(i, "duelStake", opts, def, "", p.Pos);
                S.Pr!.NewItem = opt.K;
                return;
            }
            case "duelStake":
                StartDuel(i, int.Parse(pr.NewItem!), int.Parse(opt.K));
                return;
        }
        S.Phase = "walk";
        S.Until = null;
        S.Busy = null;
        S.Then = null;
        Run(then);
    }

    void NextQueued()
    {
        if (S.Queue.Count == 0) { BeginTurn(); return; }
        var pr = S.Queue[0];
        S.Queue.RemoveAt(0);
        S.Pr = pr;
        Phase("prompt", VechirkaRules.PromptMs(pr.Kind));
    }

    // ---------- дуель ⚔ (§8.5) ----------

    IEnumerable<VechirkaPoolEntry> DuelCandidates() =>
        Pool.Where(e => e.DuelW > 0 && e.Min <= 2 && e.Max >= 2 && e.CapMs <= 60_000);

    bool Duel(int i)
    {
        var p = S.P[i];
        if (S.DuelDone || !DuelCandidates().Any() || N < 2)
        {
            Gain(i, VechirkaRules.DuelSplit);
            Say("duelSplit", ("nick", p.Name));
            return false;
        }
        var others = S.Order.Where(k => k != i).ToList();
        // типове рішення машини (бот / відпалий) — найбагатша присутня людина, щоб не вийшло бот×бот (R2 M3)
        var live = p.Machine ? others.Where(k => !S.P[k].Bot && !S.P[k].Away).ToList() : [];
        var rich = (live.Count > 0 ? live : others).OrderByDescending(k => S.P[k].Coins).First();
        var opts = others.Select(k => new VechirkaOpt(k.ToString(), S.P[k].Name)).ToList();
        Ask(i, "duelWho", opts, others.IndexOf(rich), "", p.Pos);
        return true;
    }

    void StartDuel(int a, int b, int stake)
    {
        S.DuelDone = true;
        Take(a, stake); Take(b, stake);
        Say("duel", ("nick", S.P[a].Name), ("nick2", S.P[b].Name));
        var cands = DuelCandidates().ToList();
        var pool = cands.Where(e => e.Id != S.LastDuelGame).ToList();
        if (pool.Count == 0) pool = cands;
        var pick = pool[R.Weighted([.. pool.Select(e => e.DuelW)])];
        S.LastDuelGame = pick.Id;
        S.M = NewMg(pick, [a, b], false);
        S.M.Duel = [a, b];
        S.M.Stake = stake;
        S.M.Honor = stake == 0;
        S.Pk = new VechirkaPick { Chooser = null, Options = [pick.Id], Chosen = pick.Id, Duel = true };
        Anim("roulette", a, key: pick.Id, ms: VechirkaRules.RouletteMs);
        Phase("pick", 0);
        BusyFor(VechirkaRules.RouletteMs, "card");
    }

    /// <summary>Що сказав останній тик підгри (кімнаті — чи розсилати вид/кадр).</summary>
    public TickResult LastMgTick { get; set; } = TickResult.None;

    // ---------- вибір міні-гри (§8.2) ----------

    void BeginPick(bool _)
    {
        var cands = Pool.Where(e => e.Weight > 0 && e.Min <= N && e.Max >= N).ToList();
        var byCat = cands.Where(e => S.Minis.Contains(e.Cat)).ToList();
        if (byCat.Count == 0 && cands.Count > 0 && !S.NoFun) { S.NoFun = true; Say("noMinis"); }
        if (byCat.Count > 0) cands = byCat;
        // Повтори дратують (R2 M5: Тонкий лід тричі за 10 кіл): поки є щонайменше три незіграні — пропонуємо лише їх,
        // далі — не грані останні 4 кола, і лише в крайньому разі — «не грали 2 кола» (§8.2)
        bool Ago(VechirkaPoolEntry e, int k) => !S.MgLast.TryGetValue(e.Id, out var r) || S.Round - r > k;
        var unplayed = cands.Where(e => !S.MgLast.ContainsKey(e.Id)).ToList();
        var old = cands.Where(e => Ago(e, 4)).ToList();
        var fresh = cands.Where(e => Ago(e, 2)).ToList();
        if (unplayed.Count >= 3) cands = unplayed;
        else if (old.Count >= 3) cands = old;
        else if (fresh.Count > 0) cands = fresh;
        if (cands.Count == 0)
        {
            Line("🍂 Глек сьогодні без забав: +5 усім");
            Say("noGames");
            for (var k = 0; k < N; k++) Gain(k, VechirkaRules.NoGames);
            S.Phase = "walk";
            BusyFor(VechirkaRules.LandMs, "round");
            return;
        }
        var opts = new List<string>();
        var left = cands.ToList();
        while (opts.Count < 3 && left.Count > 0)
        {
            var w = left.Select(e => Math.Max(1, e.Weight * 100 / (1 + S.MgCount.GetValueOrDefault(e.Id)))).ToList();
            var k = R.Weighted(w);
            opts.Add(left[k].Id);
            left.RemoveAt(k);
        }
        var chooser = Lasts(1)[0];
        S.Pk = new VechirkaPick { Chooser = chooser, Options = opts, Duel = false };
        S.M = null;
        Say("pick", ("nick", S.P[chooser].Name));
        if (S.Round == S.Rounds) Say("lastGame");
        Anim("pick", chooser);
        Phase("pick", VechirkaRules.PickMs);
    }

    void Choose(VechirkaPick pk, int o)
    {
        if (o < 0 || o >= pk.Options.Count) throw new GameError("Такої гри нема");
        pk.Chosen = pk.Options[o];
        var e = Pool.First(x => x.Id == pk.Chosen);
        S.M = NewMg(e, [.. Enumerable.Range(0, N)], S.Round == S.Rounds);
        Anim("roulette", pk.Chooser, key: e.Id, ms: VechirkaRules.RouletteMs);
        S.Until = null;
        S.ThinkAt = null;
        BusyFor(VechirkaRules.RouletteMs, "card");
    }

    VechirkaMgState NewMg(VechirkaPoolEntry e, int[] seats, bool x2) => new()
    {
        Id = e.Id, Title = e.Title, Howto = e.Howto, Seats = seats, X2 = x2,
        Repeat = S.MgCount.ContainsKey(e.Id),
    };

    void BeginCard()
    {
        var m = S.M!;
        Say("mgStart", ("game", m.Title));
        var ms = m.Repeat ? VechirkaRules.CardRepeatMs : VechirkaRules.CardMs;
        // дуель і є кому ставити — картка живе щонайменше BetMs, інакше бот×бот чи двоє швидких зрізають ставки
        if (m.Duel is not null && Bettors().Any())
        {
            ms = Math.Max(ms, VechirkaRules.BetMs + 2000);
            m.BetUntil = Now.AddMilliseconds(VechirkaRules.BetMs);
        }
        Phase("card", ms);
        if (CardReady()) BeginMg();
    }

    /// <summary>Присутні люди-учасники (на картці «Готовий»).</summary>
    IEnumerable<int> CardHumans() => S.M!.Seats.Where(k => !S.P[k].Bot && !S.P[k].Away);

    /// <summary>Присутні люди, що дивляться дуель і можуть поставити.</summary>
    IEnumerable<int> Bettors() =>
        S.M is { Duel: not null } m ? Enumerable.Range(0, N).Where(k => !m.Seats.Contains(k) && !S.P[k].Bot && !S.P[k].Away) : [];

    /// <summary>Картку можна закривати: учасники готові, а в дуелі — усі глядачі поставили або вікно ставок минуло.</summary>
    bool CardReady()
    {
        if (S.Phase != "card" || S.M is not { } m || S.Busy is not null || S.Paused is not null) return false;
        if (!CardHumans().All(m.Ready.Contains)) return false;
        if (m.BetUntil is not { } bu) return true;
        return Now >= bu || Bettors().All(m.Bets.ContainsKey);
    }

    void BeginMg()
    {
        var m = S.M!;
        S.MgLast[m.Id] = S.Round;
        S.MgCount[m.Id] = S.MgCount.GetValueOrDefault(m.Id) + 1;
        var bots = m.Seats.Select(k => S.P[k].Bot || S.P[k].Away).ToArray();
        S.Phase = "mg";
        S.Until = null;
        S.Busy = null;
        S.ThinkAt = null;
        m.Running = Mg.Begin(m.Id, m.Seats, bots, S.Level);
        if (!m.Running) { Results(MinigameResult.Even(m.Seats.Length, "не стартувала")); return; }
        if (Mg.Title is { Length: > 0 } t) m.Title = t;
        if (Mg.Howto is { Length: > 0 } h) m.Howto = h;
    }

    /// <summary>Крок фази mg: тик підгри; кінець — до результатів. false — чекаємо далі.</summary>
    bool MgStep()
    {
        if (S.M is not { Running: true }) { Results(MinigameResult.Even(S.M?.Seats.Length ?? N, "")); return true; }
        // накопичуємо: крок, зроблений усередині дії (Input), не має з'їсти кадр — його поверне наступний тик (R1 M1)
        var t = Mg.Tick();
        LastMgTick = new(LastMgTick.Frame | t.Frame, LastMgTick.View | t.View);
        if (Mg.Result is not { } res) return false;
        S.M.Running = false;
        Results(res);
        return true;
    }

    /// <summary>Місця й шеляги (§8.4, §7.1): рівні ділять суму місць угору; Crash — середня кожному.</summary>
    void Results(MinigameResult res)
    {
        var m = S.M!;
        var n = m.Seats.Length;
        var places = res.Places.Length == n ? res.Places : [.. Enumerable.Repeat(1, n)];
        var crash = res.How == MinigameEnd.Crash;
        int[] pay;
        if (m.Duel is not null) pay = new int[n];
        else if (crash)
        {
            var t = VechirkaRules.Payouts(n);
            var avg = (t.Sum() + n - 1) / n;
            pay = [.. Enumerable.Repeat(avg, n)];
            Say("mgBroken", ("game", m.Title));
        }
        else pay = VechirkaRules.Pay(places);
        if (m.X2) pay = [.. pay.Select(x => x * VechirkaRules.LastMgMult)];
        for (var k = 0; k < n; k++) Gain(m.Seats[k], pay[k], "mg");
        if (!crash)
            for (var k = 0; k < n; k++) if (places[k] == 1) S.P[m.Seats[k]].MgWins++;

        if (m.Duel is [var a, var b])
        {
            var tie = crash || places[0] == places[1];
            if (tie) { Gain(a, m.Stake); Gain(b, m.Stake); }
            else
            {
                var w = places[0] < places[1] ? a : b;
                if (m.Honor) Gain(w, VechirkaRules.DuelHonor, "mg");
                else Gain(w, m.Stake * 2, "mg");
                Say("duelWin", ("nick", S.P[w].Name));
                var lucky = m.Bets.Where(kv => kv.Value == w).Select(kv => kv.Key).ToList();
                foreach (var k in lucky) Gain(k, VechirkaRules.BetWin);
                if (lucky.Count > 0) Say("betWin");
                // для таблиці результатів дуелі — що виграв переможець
                pay[w == a ? 0 : 1] = m.Honor ? VechirkaRules.DuelHonor : m.Stake * 2;
            }
        }
        else if (!crash)
        {
            var win = Enumerable.Range(0, n).Where(k => places[k] == 1).Select(k => S.P[m.Seats[k]].Name).ToList();
            if (win.Count > 0) Say("mgWin", ("nick", Nicks(win)), ("game", m.Title));
        }
        m.Results = [.. Enumerable.Range(0, n).Select(k => new VechirkaMgLine(m.Seats[k], places[k], pay[k])).OrderBy(x => x.Place)];
        m.How = res.How.ToString();
        Line($"🎯 {m.Title}: " + string.Join(", ", m.Results.Select(x => S.P[x.I].Name)));
        Anim("results", null, ms: VechirkaRules.ResultsMs);
        Phase("results", VechirkaRules.ResultsMs);
    }

    void AfterResults()
    {
        var duel = S.M?.Duel is not null;
        S.Pk = null;
        if (duel) { S.Phase = "walk"; S.Until = null; BusyFor(VechirkaRules.LandMs, "endTurn"); return; }
        S.Phase = "walk";
        S.Until = null;
        S.Then = "round";
        S.Busy = Now;
    }

    // ---------- Пізній вечір (§9) ----------

    void BeginLate()
    {
        S.Late = true;
        // скарбничку розбито: нижній половині порівну, остача лишається
        var low = Lasts(N / 2);
        if (low.Count > 0 && S.Bank > 0)
        {
            var each = S.Bank / low.Count;
            if (each > 0) { foreach (var k in low) Gain(k, each); S.Bank -= each * low.Count; }
            Say("bankSplit", ("n", S.Bank.ToString()));
            Line($"🐷 Скарбничку розбито: по {each}");
        }
        var choosers = Lasts(N >= 6 ? 2 : 1);
        S.L = new VechirkaLate { Choosers = choosers };
        Say("late");
        Line("🌙 Пізній вечір");
        Anim("late", null);
        Phase("late", VechirkaRules.LateMs);
    }

    public static readonly VechirkaOpt[] LateOptions =
        [new("coins", $"{VechirkaRules.LateGiftCoins} 🪙"), new("feather", "🪶 Перо лелеки"), new("pumpkin", "🎃 Гарбуз")];

    void FinishLate()
    {
        var l = S.L!;
        foreach (var c in l.Choosers) l.Chosen.TryAdd(c, "coins");
        S.Queue.Clear();
        foreach (var c in l.Choosers)
        {
            var g = l.Chosen[c];
            Say("lateGift", ("nick", S.P[c].Name));
            if (g == "coins") { Gain(c, VechirkaRules.LateGiftCoins); continue; }
            var p = S.P[c];
            if (p.Items.Count < VechirkaRules.Hand) { p.Items.Add(g); Fx("item", c, k: g); continue; }
            var opts = p.Items.Select(k => new VechirkaOpt(k, VechirkaRules.Item[k].Icon + " " + VechirkaRules.Item[k].Name)).ToList();
            opts.Add(new VechirkaOpt(g, VechirkaRules.Item[g].Icon + " " + VechirkaRules.Item[g].Name + " (новий)"));
            S.Queue.Add(new VechirkaPrompt { Who = c, Kind = "discard", Options = opts, Default = opts.Count - 1, Then = "queue", NewItem = g });
        }
        S.Phase = "walk";
        S.Until = null;
        S.ThinkAt = null;
        BusyFor(VechirkaRules.LandMs, "queue");
    }

    // ---------- фінал (§9) ----------

    void BeginFinal()
    {
        S.Cur = null;
        var f = new VechirkaFinal();
        foreach (var key in S.BonusKeys)
        {
            int Stat(VechirkaPlayer p) => key switch
            {
                "mgwins" => p.MgWins, "earned" => p.S.Earned, "steps" => p.S.Steps, "bully" => p.S.Bully,
                "traps" => p.S.Traps, "shop" => p.S.Shop, "chests" => p.S.Chests, _ => 0,
            };
            var vals = S.P.Select(Stat).ToArray();
            var max = vals.Max();
            int[] win = max <= 0 || vals.All(v => v == max) ? [] : [.. Enumerable.Range(0, N).Where(k => vals[k] == max)];
            f.Bonuses.Add(new VechirkaBonusWin(key, VechirkaRules.BonusList.First(b => b.Key == key).Title, win));
        }
        S.F = f;
        f.Step = -1;
        Say("final");
        Phase("final", VechirkaRules.BonusMs / 2);
    }

    /// <summary>Кілька ніків в одному <c>{nick}</c> (рівні місця): «Вася, Петро і Оля».</summary>
    static string Nicks(IReadOnlyList<string> names) =>
        names.Count <= 1 ? names.FirstOrDefault() ?? "" : string.Join(", ", names.Take(names.Count - 1)) + " і " + names[^1];

    void FinalStep()
    {
        var f = S.F!;
        f.Step++;
        if (f.Step < f.Bonuses.Count)
        {
            var b = f.Bonuses[f.Step];
            foreach (var k in b.Winners) { S.P[k].Gleks++; Fx("glek", k, 1); }
            Anim("bonus", null, key: b.Key, ms: VechirkaRules.BonusMs);
            Say("bonus." + b.Key, ("title", b.Title), ("nick", Nicks(b.Winners.Select(k => S.P[k].Name).ToList())));
            if (b.Winners.Length > 0) Line($"🏺 Бонусний глек «{b.Title}»: " + string.Join(", ", b.Winners.Select(k => S.P[k].Name)));
            Phase("final", VechirkaRules.BonusMs);
            return;
        }
        if (f.Step == f.Bonuses.Count)
        {
            f.Ranking = [.. Enumerable.Range(0, N).OrderBy(PlaceOf).ThenBy(k => k).Select(k => new[] { k, PlaceOf(k) })];
            var top = Enumerable.Range(0, N).Where(k => PlaceOf(k) == 1).ToList();
            // «Камбек» — останній на половині вечора тепер у трійці (звичайна репліка; переможця ✱ скаже однаково)
            if (Enumerable.Range(0, N).FirstOrDefault(k => S.P[k].S.LastAtHalf && PlaceOf(k) <= 3, -1) is var cb and >= 0)
                Say("comeback", ("nick", S.P[cb].Name));
            if (top.Count > 1) Say("tie", ("nick", Nicks(top.Select(k => S.P[k].Name).ToList())));
            else if (S.P[top[0]].Bot) Say("winBot", ("nick", S.P[top[0]].Name));
            else Say("win", ("nick", S.P[top[0]].Name), ("n", S.P[top[0]].Gleks.ToString()));
            Anim("summary", null, ms: VechirkaRules.SummaryMs);
            Phase("final", VechirkaRules.SummaryMs);
            return;
        }
        S.Done = true;
        S.Phase = "done";
        S.Until = null;
        Line("🎉 Глечикова вечірка: " + string.Join(", ", S.F!.Ranking.Select(r => $"{S.P[r[0]].Name} — {S.P[r[0]].Gleks} 🏺")));
        Out.Add(new VechirkaOut("finish", ""));
    }

    // ---------- дії (§14.2) ----------

    /// <summary>Дія гравця P[i]. Помилка — GameError, стан не міняється.</summary>
    public void Act(int i, string action, JsonElement payload, DateTimeOffset now)
    {
        Now = now;
        if (i < 0 || i >= N) throw new GameError("Ти в цій вечірці не граєш");
        var p = S.P[i];
        // Будь-яка власна дія людини будить її («задрімав» знімається); дії мізків за неї — ні.
        if (!_machine && !p.Bot) { p.Misses = 0; if (p.Auto) { p.Auto = false; ScheduleMachine(); } }
        switch (action)
        {
            case "here": p.Auto = false; p.Misses = 0; return;
            case "roll":
                RequireTurn(i, "turn");
                DoRoll(null);
                break;
            case "item":
                RequireTurn(i, "turn");
                UseItem(i, Str(payload, "k") ?? throw new GameError("Який предмет?"));
                break;
            case "aim":
                RequireTurn(i, "aim");
                var t = Int(payload, "target"); var node = Str(payload, "node"); var n = Int(payload, "n");
                if (t is null && node is null && n is null) { S.Am = null; Phase("turn", TurnLeftMs()); break; }
                DoAim(i, t, node, n);
                break;
            case "pick":
            {
                var o = Int(payload, "o") ?? throw new GameError("Що обираєш?");
                if (S.Paused is not null) throw new GameError("Пауза");
                if (S.Phase == "prompt" && S.Pr is { } pr && pr.Who == i) { Answer(pr, o); break; }
                if (S.Phase == "pick" && S.Pk is { Chosen: null } pk && pk.Chooser == i) { Choose(pk, o); break; }
                if (S.Phase == "late" && S.L is { } l && l.Choosers.Contains(i) && S.Busy is null)
                {
                    if (o < 0 || o >= LateOptions.Length) throw new GameError("Такого подарунка нема");
                    l.Chosen[i] = LateOptions[o].K;
                    if (l.Choosers.All(l.Chosen.ContainsKey)) FinishLate();
                    break;
                }
                throw new GameError("Зараз не твій вибір");
            }
            case "ready":
            {
                if (S.Phase != "card" || S.M is not { } m || !m.Seats.Contains(i)) throw new GameError("Зараз нема на що готуватись");
                if (S.Paused is not null) throw new GameError("Пауза");
                if (!m.Ready.Contains(i)) m.Ready.Add(i);
                if (CardReady()) BeginMg();
                break;
            }
            case "bet":
            {
                if (S.Phase != "card" || S.M is not { Duel: [var a, var b] } m) throw new GameError("Ставки — лише на дуель");
                if (i == a || i == b || p.Bot) throw new GameError("На себе не ставлять");
                var w = Int(payload, "i") ?? -1;
                if (w != a && w != b) throw new GameError("Став на одного з дуелянтів");
                if (S.Paused is not null) throw new GameError("Пауза");
                m.Bets[i] = w;
                if (CardReady()) BeginMg();
                break;
            }
            case "mg":
            {
                if (S.Phase != "mg" || S.M is not { Running: true } m || !m.Seats.Contains(i)) throw new GameError("Ти в цій міні-грі не граєш");
                var a = Str(payload, "a") ?? "";
                var sub = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("p", out var pp) ? pp : default;
                var r = Mg.Act(i, a, sub);
                if (!r.Ok) throw new GameError(r.Message);
                break;
            }
            default: throw new GameError("Тут так не ходять");
        }
        if (!_machine) Advance(now);
    }

    void RequireTurn(int i, string phase)
    {
        if (S.Paused is not null) throw new GameError("Пауза");
        if (S.Phase != phase || S.Cur != i || S.Busy is not null) throw new GameError("Зараз не твій хід");
    }

    static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? Int(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : null;

    // ---------- пауза ----------

    /// <summary>Пауза господаря чи «стіл порожній» (null — зняти). Таймер поточної фази після паузи — заново повний.</summary>
    public void SetPause(string? kind)
    {
        if (S.Paused == kind) return;
        S.Paused = kind;
        S.PausedAt = kind is null ? null : Now;
        if (kind is null)
        {
            if (S.Until is not null && S.Total > 0) S.Until = Now.AddMilliseconds(S.Total);
            if (S.Busy is not null) S.Busy = Now;
            ScheduleMachine();
        }
    }

    /// <summary>Машинний стан людини змінився (відпала, повернулась): перепланувати думання.</summary>
    public void Replan(DateTimeOffset now) { Now = now; if (S.ThinkAt is null) ScheduleMachine(); else { S.ThinkAt = null; ScheduleMachine(); } }

    // ---------- Save / Load (§17) ----------

    static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    public string Save()
    {
        S.Rng = R.State;
        return JsonSerializer.Serialize(S, Json);
    }

    /// <summary>
    /// Відновити: mg → card тієї ж міні-гри (переграють з початку); рух — далі з рештою кроків; таймери — повні від now.
    /// </summary>
    public static VechirkaCore Load(string json, VechirkaMap map, IMgRunner mg, IReadOnlyList<VechirkaPoolEntry> pool, DateTimeOffset now)
    {
        var s = JsonSerializer.Deserialize<VechirkaState>(json, Json) ?? throw new InvalidDataException("порожній стан вечірки");
        var c = new VechirkaCore(map, s, mg, pool) { Now = now };
        if (s.Phase == "mg" && s.M is { } m)
        {
            m.Running = false; m.Ready.Clear();
            c.Phase("card", VechirkaRules.CardMs);
        }
        else if (s.Until is not null) s.Until = now.AddMilliseconds(Math.Max(1, s.Total));
        if (s.Busy is not null) s.Busy = now;
        if (s.Paused is not null) s.PausedAt = now;
        s.ThinkAt = null;
        if (s.Phase != "mg") c.ScheduleMachine();
        return c;
    }
}
