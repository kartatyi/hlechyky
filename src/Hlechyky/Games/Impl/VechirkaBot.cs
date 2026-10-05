using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот дошки (§11): думає раз, коли настала його черга, оцінки — на графі карти (BFS). Рівень — рівень столу.
/// Діє тими самими діями, що й людина (<see cref="VechirkaCore.Act"/>); рандом — ГВЧ вечірки.
/// </summary>
public sealed class VechirkaBot : IVechirkaBrain
{
    static readonly string[] ShopPriority = ["pick", "horse", "pan", "charm", "fork", "rope", "gate", "key"];
    static readonly Dictionary<string, int> Worth = new()
    {
        ["feather"] = 9, ["pumpkin"] = 8, ["pick"] = 7, ["fork"] = 6, ["pan"] = 5, ["charm"] = 5,
        ["horse"] = 4, ["rope"] = 4, ["gate"] = 3, ["key"] = 2,
    };

    public void Think(VechirkaCore c, int i)
    {
        var lvl = c.S.Level;
        switch (c.S.Phase)
        {
            case "turn": Turn(c, i, lvl); break;
            case "aim":
                // прицілювання зависло (не мало б) — скасувати й кинути, щоб не ходити по колу
                c.Act(i, "aim", El(new { }), c.Now);
                Do(c, i, "roll", new { });
                break;
            case "prompt": Prompt(c, i, lvl); break;
            case "pick": if (c.S.Pk is { } pk) Do(c, i, "pick", new { o = c.Rand(pk.Options.Count) }); break;
            case "late": Do(c, i, "pick", new { o = Late(c, i, lvl) }); break;
        }
    }

    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);
    static void Do(VechirkaCore c, int i, string a, object p) => c.Act(i, a, El(p), c.Now);

    /// <summary>Кроки вперед до лавки Глека (пором — коли є ключ або вистачає й на пором, і на глек).</summary>
    public static int D(VechirkaCore c, int i)
    {
        var p = c[i];
        var ferry = p.Items.Contains("key") || p.Coins >= 10 + c.Price;
        return c.Map.Forward(p.Pos, c.S.Stand, ferry) ?? c.Map.Forward(p.Pos, c.S.Stand, true) ?? 99;
    }

    void Turn(VechirkaCore c, int i, LiveBots.Level lvl)
    {
        var p = c[i];
        var usable = p.Items.Where(k => c.Usable(i, k)).Distinct().ToList();
        if (usable.Count == 0) { Do(c, i, "roll", new { }); return; }
        if (lvl == LiveBots.Level.Easy)
        {
            if (c.Rand(10) < 4) { Do(c, i, "roll", new { }); return; }
            var k = usable[c.Rand(usable.Count)];
            Use(c, i, k, lvl, random: true);
            return;
        }
        var d = D(c, i);
        var others = Enumerable.Range(0, c.N).Where(k => k != i).ToList();
        var leader = c.LeaderIdx();
        foreach (var k in usable)
        {
            var ok = k switch
            {
                "feather" => p.Coins >= c.Price,
                "pumpkin" => others.Any(o => D(c, o) + 8 <= d),
                "pan" => c.AimFor(i, "pan")?.Targets?.Any(t => Fair(c, t, lvl)) == true,
                "fork" => others.Any(o => c[o].Coins >= 7 && Fair(c, o, lvl)),
                "pick" => p.Coins >= c.Price && d is >= 2 and <= 12 && d <= (p.Bump ? 6 : 12),
                "horse" => d is >= 8 and <= 18,
                "rope" => leader is { } l && l != i && D(c, l) <= 8 && d > 12 && Fair(c, l, lvl),
                "gate" => others.Any(o => D(c, o) <= 12 && D(c, o) < d),
                _ => false,
            };
            if (ok && Use(c, i, k, lvl, random: false)) return;
        }
        if (c.S.Phase == "turn" && c.S.Cur == i) Do(c, i, "roll", new { });
    }

    /// <summary>Сильний не б'є того, в кого оберіг.</summary>
    static bool Fair(VechirkaCore c, int t, LiveBots.Level lvl) => lvl != LiveBots.Level.Hard || !c[t].Charm;

    bool Use(VechirkaCore c, int i, string k, LiveBots.Level lvl, bool random)
    {
        Do(c, i, "item", new { k });
        if (c.S.Phase != "aim" || c.S.Am is not { } a) return true;
        if (a.Range is { } r)
        {
            var n = random ? c.Rand(r[1]) + 1 : Math.Clamp(D(c, i), r[0], r[1]);
            Do(c, i, "aim", new { n });
            return true;
        }
        if (a.Nodes is { Count: > 0 } nodes)
        {
            string node = nodes[c.Rand(nodes.Count)];
            if (!random)
            {
                // на шляху найближчого до лавки суперника
                var foe = Enumerable.Range(0, c.N).Where(o => o != i).OrderBy(o => D(c, o)).First();
                var path = c.Map.ForwardPath(c[foe].Pos, c.S.Stand, true) ?? [];
                node = path.FirstOrDefault(nodes.Contains) ?? node;
            }
            Do(c, i, "aim", new { node });
            return true;
        }
        if (a.Targets is { Count: > 0 } ts)
        {
            int t;
            if (random) t = ts[c.Rand(ts.Count)];
            else
            {
                var fair = ts.Where(x => Fair(c, x, lvl)).ToList();
                if (fair.Count == 0) fair = ts;
                t = k switch
                {
                    "pumpkin" => fair.OrderBy(x => D(c, x)).First(),
                    "rope" => c.LeaderIdx() is { } l && fair.Contains(l) ? l : fair[0],
                    _ => fair.OrderByDescending(x => c.LeaderIdx() == x ? 1 : 0).ThenByDescending(x => c[x].Coins).First(),
                };
            }
            Do(c, i, "aim", new { target = t });
            return true;
        }
        Do(c, i, "aim", new { });
        return false;
    }

    void Prompt(VechirkaCore c, int i, LiveBots.Level lvl)
    {
        var pr = c.S.Pr!;
        var p = c[i];
        var opts = pr.Options;
        int Ix(string k) => opts.FindIndex(o => o.K == k);
        var o = pr.Default;
        var easy = lvl == LiveBots.Level.Easy;
        switch (pr.Kind)
        {
            case "fork":
                o = easy ? c.Rand(opts.Count) : Best(c, i, opts);
                break;
            case "ferry":
            {
                if (easy) break;
                var road = c.Map.Forward(p.Pos, c.S.Stand, false) ?? 99;
                var boat = c.Map.Forward(p.Pos, c.S.Stand, true) ?? 99;
                var saves = road - boat >= 8 || (lvl == LiveBots.Level.Hard && boat <= 21 && road > boat);
                if (!saves) break;
                var key = opts.FindIndex(x => x.K.StartsWith("key:") && x.Ok);
                var pay = opts.FindIndex(x => x.K.StartsWith("pay:") && x.Ok);
                if (key >= 0) o = key;
                else if (pay >= 0 && p.Coins - 10 >= c.Price) o = pay;
                break;
            }
            case "stand": o = Ix("buy"); break;
            case "shop":
            {
                var ok = opts.Where(x => x.Ok && x.Price is not null).ToList();
                if (easy) { if (ok.Count > 0 && c.Rand(2) == 0) o = Ix(ok[c.Rand(ok.Count)].K); break; }
                var far = D(c, i) > 18;
                foreach (var k in ShopPriority)
                {
                    var x = ok.FirstOrDefault(y => y.K == k);
                    if (x is null || (p.Coins - x.Price < 20 && !far)) continue;
                    if (k == "key" && lvl == LiveBots.Level.Hard && c.Map.Forward(p.Pos, c.S.Stand, false) is var road
                        && c.Map.Forward(p.Pos, c.S.Stand, true) is var boat && road == boat) continue;
                    o = Ix(k);
                    break;
                }
                break;
            }
            case "discard":
                if (easy) break;
                o = Enumerable.Range(0, opts.Count).OrderBy(k => Worth.GetValueOrDefault(opts[k].K)).First();
                break;
            case "duelWho":
                if (easy) { o = c.Rand(opts.Count); break; }
                if (lvl == LiveBots.Level.Hard)
                {
                    var topWins = Enumerable.Range(0, c.N).Max(k => c[k].MgWins);
                    var cand = Enumerable.Range(0, opts.Count).Where(k => c[int.Parse(opts[k].K)].MgWins < topWins || topWins == 0).ToList();
                    if (cand.Count > 0) o = cand.OrderByDescending(k => c[int.Parse(opts[k].K)].Coins).First();
                }
                break;
            case "duelStake":
            {
                var want = easy ? 5 : lvl == LiveBots.Level.Hard && p.Coins >= 30 ? 20 : 10;
                var fit = Enumerable.Range(0, opts.Count).Where(k => opts[k].Ok && int.Parse(opts[k].K) <= want).ToList();
                if (fit.Count > 0) o = fit.Last();
                break;
            }
        }
        if (o < 0 || o >= opts.Count || !opts[o].Ok) o = pr.Default;
        Do(c, i, "pick", new { o });
    }

    /// <summary>Розвилка: мінімум кроків до лавки.</summary>
    static int Best(VechirkaCore c, int i, List<VechirkaOpt> opts)
    {
        var best = 0; var bd = int.MaxValue;
        for (var k = 0; k < opts.Count; k++)
        {
            var to = opts[k].K.Split(':')[1];
            var d = 1 + (c.Map.Forward(to, c.S.Stand, false) ?? 99);
            if (d < bd) { bd = d; best = k; }
        }
        return best;
    }

    static int Late(VechirkaCore c, int i, LiveBots.Level lvl)
    {
        if (lvl == LiveBots.Level.Easy) return 0;
        var p = c[i];
        if (p.Items.Count >= VechirkaRules.Hand) return 0;
        if (p.Coins >= c.Price) return 1;
        if (lvl == LiveBots.Level.Hard && Enumerable.Range(0, c.N).Any(o => o != i && D(c, o) + 8 <= D(c, i))) return 2;
        return 0;
    }
}
