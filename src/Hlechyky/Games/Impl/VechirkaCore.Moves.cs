namespace Hlechyky.Games.Impl;

// Хід: кидок, рух до зупинки, клітинки, предмети, події (§2, §4–§6).
public sealed partial class VechirkaCore
{
    void BeginTurn()
    {
        if (S.TurnIdx >= S.Order.Length) { BeginPick(false); return; }
        S.Cur = S.Order[S.TurnIdx];
        S.T = new VechirkaTurn();
        S.Pr = null; S.Am = null;
        if (S.Round == 1 && S.TurnIdx == 0) Say("turnFirst", ("nick", S.P[S.Cur.Value].Name));
        Anim("turn", S.Cur);
        Phase("turn", VechirkaRules.TurnMs);
    }

    void EndTurn()
    {
        S.Pr = null; S.Am = null;
        S.Phase = "walk";
        S.Until = null;
        BusyFor(VechirkaRules.EndTurnMs, "nextTurn");
    }

    void NextTurn()
    {
        if (S.Cur is { } c) S.P[c].Moved = true;
        S.TurnIdx++;
        if (S.TurnIdx >= S.Order.Length) { S.Cur = null; BeginPick(false); return; }
        BeginTurn();
    }

    /// <summary>Кидок: n — вибирайко (замість кубиків), null — кубики (гуля — один, підкова — +1).</summary>
    void DoRoll(int? n)
    {
        var i = S.Cur!.Value;
        var p = S.P[i];
        int[] dice;
        int steps;
        if (n is { } x) { dice = []; steps = x; }
        else
        {
            var count = p.Bump ? 1 : 2;
            if (S.T.Horse) count++;
            dice = [.. Enumerable.Range(0, count).Select(_ => R.Next(1, 7))];
            steps = dice.Sum();
        }
        p.Bump = false;
        S.T.Rolled = true;
        S.T.Dice = dice;
        S.T.Steps = steps;
        S.Am = null;
        Anim("dice", i, dice: dice);
        if (dice.Length == 2 && dice[0] == dice[1])
        {
            Gain(i, VechirkaRules.Double);
            if (R.Next(3) == 0) Say("double", ("nick", p.Name));
        }
        S.Phase = "walk";
        S.Until = null;
        BusyFor(n is null ? VechirkaRules.DiceMs : 300, "move");
    }

    /// <summary>
    /// Рух до першої зупинки (§2.2): розвилка, лавка Глека, крамниця, шлагбаум, кінець кроків. Прохідні клітинки
    /// (Криниця, скарбничка) діють одразу.
    /// </summary>
    void Move()
    {
        var i = S.Cur!.Value;
        var p = S.P[i];
        var t = S.T;
        var path = new List<string>();
        t.Stop = "";
        while (t.Steps > 0)
        {
            var exits = Map.Next[p.Pos];
            string next;
            if (exits.Length > 1)
            {
                if (t.Hop is null) { t.Stop = "fork"; break; }
                next = t.Hop; t.Hop = null;
            }
            else next = exits[0];
            p.Pos = next;
            t.Steps--;
            p.S.Steps++;
            path.Add(next);
            var node = Map[next];
            if (S.Gates.FirstOrDefault(g => g.Node == next) is { } gate && gate.Owner != i)
            {
                GateHit(i, gate);
                t.Steps = 0;
                break;
            }
            if (t.Steps > 0)
            {
                if (node.Type == "start") p.Bump = false;
                if (node.Type == "bank") ToBank(i, VechirkaRules.BankPass);
            }
            if (next == S.Stand && !t.Bought)
            {
                if (p.Coins >= Price) { t.Stop = "stand"; break; }
                if (!t.Broke) { t.Broke = true; Say("broke", ("nick", p.Name)); }
            }
            if (node.Type == "shop" && CanShop(i)) { t.Stop = "shop"; break; }
        }
        if (t.Stop == "" ) t.Stop = "land";
        if (path.Count == 0) { AtStop(); return; }
        Anim("walk", i, path: path, ms: VechirkaRules.StepMs * path.Count + VechirkaRules.WalkTailMs);
        S.Phase = "walk";
        S.Until = null;
        BusyFor(VechirkaRules.StepMs * path.Count + VechirkaRules.WalkTailMs, "stop");
    }

    bool CanShop(int i)
    {
        var p = S.P[i];
        if (S.T.ShopSkip || p.Items.Count >= VechirkaRules.Hand || S.T.Asked == p.Pos) return false;
        return S.Shops.TryGetValue(p.Pos, out var goods) && goods.Any(g => VechirkaRules.Item[g].Price <= p.Coins);
    }

    void AtStop()
    {
        var i = S.Cur!.Value;
        var p = S.P[i];
        switch (S.T.Stop)
        {
            case "fork":
            {
                var opts = new List<VechirkaOpt>();
                var forks = Map.Forks[p.Pos];
                var ferry = false;
                foreach (var f in forks)
                {
                    if (Map.GateOf(p.Pos, f.To) is { } g)
                    {
                        ferry = true;
                        opts.Add(new VechirkaOpt("pay:" + f.To, $"⛴ Пливемо ({g.Cost})", p.Coins >= g.Cost, g.Cost));
                        if (g.Key) opts.Add(new VechirkaOpt("key:" + f.To, "🗝 Ключем", p.Items.Contains("key")));
                    }
                    else opts.Add(new VechirkaOpt("go:" + f.To, f.Label));
                }
                // типово — перший безплатний («Шляхом»)
                var def = opts.FindIndex(o => o.K.StartsWith("go:"));
                if (ferry)
                {
                    // «Шляхом» — останнім, як у таблиці §2.3
                    var go = opts.Where(o => o.K.StartsWith("go:")).ToList();
                    opts = [.. opts.Where(o => !o.K.StartsWith("go:")), .. go];
                    def = opts.FindIndex(o => o.K.StartsWith("go:"));
                }
                Ask(i, ferry ? "ferry" : "fork", opts, Math.Max(0, def), "move", p.Pos);
                break;
            }
            case "stand":
                Ask(i, "stand", [new("buy", $"🏺 Купити за {Price}"), new("no", "Не треба")], 0,
                    S.T.Steps > 0 ? "move" : "land", p.Pos);
                break;
            case "shop":
            {
                S.T.Asked = p.Pos;
                var goods = S.Shops[p.Pos];
                var opts = goods.Select(g =>
                {
                    var it = VechirkaRules.Item[g];
                    return new VechirkaOpt(g, $"{it.Icon} {it.Name}", it.Price <= p.Coins, it.Price);
                }).ToList();
                opts.Add(new("none", "Нічого"));
                opts.Add(new("skip", "Нічого й не питати до кінця ходу"));
                Ask(i, "shop", opts, goods.Length, S.T.Steps > 0 ? "move" : "land", p.Pos);
                break;
            }
            default: Land(); break;
        }
    }

    void Ask(int who, string kind, List<VechirkaOpt> opts, int def, string then, string? node = null, string? newItem = null)
    {
        S.Pr = new VechirkaPrompt { Who = who, Kind = kind, Options = opts, Default = def, Then = then, Node = node, NewItem = newItem };
        Phase("prompt", VechirkaRules.PromptMs(kind));
    }

    /// <summary>Став (§4). Після дії клітинки — кінець ходу, якщо клітинка не відкрила нового рішення.</summary>
    void Land()
    {
        var i = S.Cur!.Value;
        var p = S.P[i];
        var node = Map[p.Pos];
        var mult = S.Late ? VechirkaRules.LateMult : 1;
        var ms = VechirkaRules.LandMs;
        S.Pr = null;
        Anim("land", i, key: node.Type);
        var type = node.Type;
        if (S.T.Wind && type == "event") type = "windcoin";
        switch (type)
        {
            case "start":
                Gain(i, VechirkaRules.StartLand * mult); p.Bump = false; break;
            case "windcoin":
                Gain(i, 3 * mult); break;
            case "coin":
                Gain(i, node.V * mult); break;
            case "trap":
            {
                p.S.Traps++;
                if (node.V != 0) ToBank(i, -node.V * mult);
                if (node.Bump) Bump(i, null);
                if (R.Next(3) == 0) Say("trap", ("nick", p.Name));
                break;
            }
            case "church":
                Gain(i, VechirkaRules.Church); p.Bump = false; break;
            case "bank":
            {
                var x = Math.Min(VechirkaRules.BankCap, S.Bank);
                if (x > 0)
                {
                    S.Bank -= x; Gain(i, x); p.S.Banks++;
                    if (x >= 10) Say("bank", ("nick", p.Name), ("n", x.ToString()));
                    Line($"🐷 {p.Name} зірвав скарбничку: +{x}");
                }
                break;
            }
            case "chest":
            {
                p.S.Chests++;
                if (R.Next(100) < VechirkaRules.ChestEmptyPct)
                {
                    Gain(i, VechirkaRules.ChestEmptyCoins);
                    Say("chestEmpty", ("nick", p.Name));
                    break;
                }
                var item = DrawItem(i);
                Say("chest", ("nick", p.Name), ("item", VechirkaRules.Item[item].Name));
                if (GiveItem(i, item, "endTurn")) return;
                break;
            }
            case "event":
                ms = VechirkaRules.EventMs;
                if (Event(i, node)) return;
                break;
            case "duel":
                if (Duel(i)) return;
                break;
        }
        S.Phase = "walk";
        S.Until = null;
        BusyFor(ms, "endTurn");
    }

    /// <summary>Гуля (Оберіг рятує). by — хто вдарив (для статистики й реплік), null — клітинка/собака.</summary>
    void Bump(int i, int? by)
    {
        var p = S.P[i];
        if (p.Charm) { BurnCharm(i); return; }
        p.Bump = true;
        Fx("bump", i);
        Say("bump", ("nick", p.Name));
    }

    void BurnCharm(int i)
    {
        S.P[i].Items.Remove("charm");
        Fx("charm", i);
        Say("charm", ("nick", S.P[i].Name));
    }

    /// <summary>Предмет зі скрині/подарунка за вагами й місцем (§6.2).</summary>
    string DrawItem(int i)
    {
        var lead = LeaderIdx() == i;
        var low = LowerHalf().Contains(i);
        var w = VechirkaRules.ChestWeights.Select(x =>
            x.Key is "pumpkin" or "feather" ? (lead ? 0 : low ? x.W * 3 : x.W) : x.W).ToList();
        return VechirkaRules.ChestWeights[R.Weighted(w)].Key;
    }

    /// <summary>Дати предмет; рука повна — питаємо, що викинути (true — відкрито prompt, далі then).</summary>
    bool GiveItem(int i, string item, string then)
    {
        var p = S.P[i];
        if (p.Items.Count < VechirkaRules.Hand) { p.Items.Add(item); Fx("item", i, k: item); return false; }
        var opts = p.Items.Select(k => new VechirkaOpt(k, VechirkaRules.Item[k].Icon + " " + VechirkaRules.Item[k].Name)).ToList();
        opts.Add(new VechirkaOpt(item, VechirkaRules.Item[item].Icon + " " + VechirkaRules.Item[item].Name + " (новий)"));
        Ask(i, "discard", opts, opts.Count - 1, then, newItem: item);
        return true;
    }

    void GateHit(int i, VechirkaGateSt gate)
    {
        var p = S.P[i];
        S.Gates.Remove(gate);
        if (p.Charm) { BurnCharm(i); return; }
        Transfer(i, gate.Owner, VechirkaRules.GateToll);
        S.P[gate.Owner].S.Bully++;
        Say("gateHit", ("nick", p.Name), ("nick2", S.P[gate.Owner].Name));
        Line($"🚧 {p.Name} уперся в шлагбаум {S.P[gate.Owner].Name}");
    }

    // ---------- події ----------

    /// <summary>Наступна подія клітинки «?» замість колоди — лише для тестів (у Save не йде).</summary>
    public string? NextEvent { get; set; }

    bool Event(int i, VechirkaNode node)
    {
        var p = S.P[i];
        string key;
        if (node.Id == "p1" || node.Name == "Водяник")
        {
            Say("ev.water", ("nick", p.Name));
            Ask(i, "event", [new("pay", "Віддати 3 шеляги"), new("bump", "🤕 Гуля")], 0, "endTurn", node.Id);
            S.Pr!.NewItem = "water";
            return true;
        }
        if (node.Name == "Карусель" || node.Id == "y2") key = "wheel";
        else if (NextEvent is { } forced) { key = forced; NextEvent = null; }
        else key = DrawEvent();
        S.LastEvent = key;
        var ev = VechirkaRules.Events.First(e => e.Key == key);
        Anim("event", i, key: key, ms: VechirkaRules.EventMs);
        Line($"❔ {p.Name}: {ev.Title}");
        Say("ev." + key, ("nick", p.Name));
        switch (key)
        {
            case "fair": for (var k = 0; k < N; k++) Gain(k, 3); break;
            case "swap": SwapAll(); break;
            case "rain": for (var k = 0; k < N; k++) ToBank(k, 2); break;
            case "gift": return GiveItem(i, DrawItem(i), "endTurn");
            case "wind":
                S.T.Wind = true;
                S.T.Steps = R.Next(3, 7);
                S.Phase = "walk";
                BusyFor(VechirkaRules.EventMs, "move");
                return true;
            case "dog": Bump(i, null); break;
            case "wedding": for (var k = 0; k < N; k++) if (k != i) Transfer(k, i, 2); break;
            case "wheel":
                switch (R.Next(4))
                {
                    case 0: Gain(i, 10); break;
                    case 1: ToBank(i, 5); break;
                    case 2: return GiveItem(i, DrawItem(i), "endTurn");
                    default: Gain(i, 3); break;
                }
                break;
            case "poor":
            {
                var min = S.P.Min(x => x.Coins);
                for (var k = 0; k < N; k++) if (S.P[k].Coins == min) Gain(k, 8);
                break;
            }
            case "move": MoveStand(); break;
            case "sale": S.SaleUntil = S.Round + 1; break;
            case "tax":
            {
                var rich = S.Order.OrderByDescending(k => S.P[k].Coins).First();
                ToBank(rich, S.P[rich].Coins / 4);
                break;
            }
        }
        return false;
    }

    string DrawEvent()
    {
        string Draw()
        {
            var w = VechirkaRules.Events.Select(e => e.Key == "swap" && S.Round <= 2 ? 0 : e.Weight).ToList();
            return VechirkaRules.Events[R.Weighted(w)].Key;
        }
        var k = Draw();
        if (k == S.LastEvent) k = Draw();
        return k;
    }

    /// <summary>«Глек напився»: перестановка без нерухомих точок (телепорт — клітинки не діють).</summary>
    void SwapAll()
    {
        if (N < 2) return;
        var pos = S.P.Select(p => p.Pos).ToArray();
        int[] perm;
        do
        {
            perm = [.. Enumerable.Range(0, N)];
            for (var k = N - 1; k > 0; k--) { var j = R.Next(k + 1); (perm[k], perm[j]) = (perm[j], perm[k]); }
        } while (Enumerable.Range(0, N).Any(k => perm[k] == k));
        for (var k = 0; k < N; k++) S.P[k].Pos = pos[perm[k]];
    }

    /// <summary>Лавка переїжджає (§6.4): ≠ поточного, далеко, без фішок і шлагбаума; нема — м'якші умови.</summary>
    void MoveStand()
    {
        var cur = S.Stand;
        var others = Map.Stands.Where(s => s != cur).ToList();
        var occupied = S.P.Select(p => p.Pos).ToHashSet();
        var gated = S.Gates.Select(g => g.Node).ToHashSet();
        var ok = others.Where(s => Map.Dist(cur, s) >= VechirkaRules.StandMinDist && !occupied.Contains(s) && !gated.Contains(s)).ToList();
        if (ok.Count == 0) ok = [.. others.Where(s => !gated.Contains(s))];
        if (ok.Count == 0) ok = others;
        if (ok.Count == 0) return;
        S.Stand = ok[R.Next(ok.Count)];
        S.Gates.RemoveAll(g => g.Node == S.Stand);
        Say("buyMove");
    }

    // ---------- предмети ----------

    /// <summary>Цілі предмета (§6.1) для аim і для ботів.</summary>
    public VechirkaAim? AimFor(int i, string item)
    {
        var p = S.P[i];
        var others = Enumerable.Range(0, N).Where(k => k != i).ToList();
        switch (item)
        {
            case "pan":
            {
                var d = Map.Undirected(p.Pos);
                var t = others.Where(k => d.TryGetValue(S.P[k].Pos, out var x) && x <= VechirkaRules.PanRange).ToList();
                return t.Count == 0 ? null : new VechirkaAim { Item = item, Targets = t };
            }
            case "fork" or "rope" or "pumpkin":
                return others.Count == 0 ? null : new VechirkaAim { Item = item, Targets = others };
            case "pick":
                return new VechirkaAim { Item = item, Range = [1, p.Bump ? 6 : 12] };
            case "gate":
            {
                var d = Map.Undirected(p.Pos);
                var occ = S.P.Select(x => x.Pos).ToHashSet();
                var nodes = Map.Nodes.Where(n => d.TryGetValue(n.Id, out var x) && x >= 1 && x <= VechirkaRules.GateRange
                        && VechirkaRules.GateTypes.Contains(n.Type) && n.Id != S.Stand && !occ.Contains(n.Id)
                        && S.Gates.All(g => g.Node != n.Id))
                    .OrderBy(n => d[n.Id]).ThenBy(n => n.Id, StringComparer.Ordinal).Select(n => n.Id).ToList();
                return nodes.Count == 0 ? null : new VechirkaAim { Item = item, Nodes = nodes };
            }
            default: return null;
        }
    }

    /// <summary>Чи можна вжити предмет у фазі turn.</summary>
    public bool Usable(int i, string item)
    {
        var p = S.P[i];
        if (S.T.UsedItem || S.T.Rolled || !p.Items.Contains(item)) return false;
        return item switch
        {
            "horse" => true,
            "feather" => Map.Prev[S.Stand].Length == 1 && Map.Prev[S.Stand][0] != p.Pos,
            "key" or "charm" => false,
            _ => AimFor(i, item) is not null,
        };
    }

    void UseItem(int i, string item)
    {
        var p = S.P[i];
        if (!Usable(i, item)) throw new GameError("Цей предмет зараз не вжити");
        if (VechirkaRules.Item[item].Aim != "")
        {
            S.Am = AimFor(i, item);
            Phase("aim", VechirkaRules.AimMs);
            return;
        }
        p.Items.Remove(item);
        S.T.UsedItem = true;
        switch (item)
        {
            case "horse": S.T.Horse = true; Fx("item", i, k: item); break;
            case "feather":
                p.Pos = Map.Prev[S.Stand][0];
                Anim("fly", i, path: [p.Pos], key: item, ms: VechirkaRules.ItemMs);
                Say("feather", ("nick", p.Name));
                break;
        }
        Phase("turn", (int)Math.Max(1000, (S.Until is { } u ? (u - Now).TotalMilliseconds : VechirkaRules.TurnMs)));
    }

    /// <summary>Ціль обрано (aim): гравець, клітинка або число.</summary>
    void DoAim(int i, int? target, string? node, int? n)
    {
        var a = S.Am!;
        var p = S.P[i];
        switch (a.Item)
        {
            case "pick":
                if (n is not { } x || x < a.Range![0] || x > a.Range[1]) throw new GameError("Таке число не вибрати");
                p.Items.Remove("pick"); S.T.UsedItem = true;
                Say("pick", ("nick", p.Name), ("n", x.ToString()));
                DoRoll(x);
                return;
            case "gate":
                if (node is null || !a.Nodes!.Contains(node)) throw new GameError("Сюди шлагбаум не можна");
                p.Items.Remove("gate"); S.T.UsedItem = true;
                S.Gates.RemoveAll(g => g.Owner == i);
                S.Gates.Add(new VechirkaGateSt(node, i));
                Anim("gate", i, path: [node], ms: VechirkaRules.ItemMs);
                Say("gate", ("nick", p.Name));
                break;
            default:
            {
                if (target is not { } t || !a.Targets!.Contains(t)) throw new GameError("Цю ціль не вибрати");
                p.Items.Remove(a.Item); S.T.UsedItem = true;
                var q = S.P[t];
                Anim("item", i, key: a.Item, to: t, ms: VechirkaRules.ItemMs);
                if (q.Charm) { BurnCharm(t); break; }
                p.S.Bully++;
                switch (a.Item)
                {
                    case "pan": q.Bump = true; Fx("bump", t); Transfer(t, i, VechirkaRules.PanCoins); p.S.Pans++; break;
                    case "fork": Transfer(t, i, VechirkaRules.ForkCoins); break;
                    case "rope": q.Pos = p.Pos; break;
                    case "pumpkin": (q.Pos, p.Pos) = (p.Pos, q.Pos); break;
                }
                Say(a.Item, ("nick", p.Name), ("nick2", q.Name));
                Line($"{VechirkaRules.Item[a.Item].Icon} {p.Name} → {q.Name}");
                break;
            }
        }
        S.Am = null;
        Phase("turn", VechirkaRules.TurnMs);
    }
}
