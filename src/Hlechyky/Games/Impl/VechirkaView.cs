namespace Hlechyky.Games.Impl;

/// <summary>
/// Вид вечірки (§14.1): усе публічне; відмінне для місця — лише <c>you</c> і <c>mg.view</c>. Таємне (номінації до
/// фіналу, ГВЧ) — ніде. Поля дошки (shops, gates, log, fx) у фазі mg не шлемо — вид легший.
/// </summary>
public static class VechirkaView
{
    public static object Build(Vechirka g, VechirkaCore c, int? me, DateTimeOffset now)
    {
        var s = c.S;
        var mg = s.Phase == "mg";
        var order = s.Order.Length == c.N ? s.Order : [.. Enumerable.Range(0, c.N)];
        var players = order.Select(i =>
        {
            var p = s.P[i];
            return new
            {
                i, nick = p.Nick, bot = p.Bot, name = p.Name, away = p.Away, auto = p.Auto, pos = p.Pos,
                gleks = p.Gleks, coins = p.Coins, items = p.Items, bump = p.Bump, charm = p.Charm,
                mgWins = p.MgWins, place = c.PlaceOf(i), color = p.Color,
            };
        }).ToList();

        object? you = null;
        if (me is { } m)
        {
            var can = new List<string>();
            if (s.Paused is null && s.Busy is null)
            {
                if (s.Phase == "turn" && s.Cur == m) { can.Add("roll"); if (s.P[m].Items.Any(k => c.Usable(m, k))) can.Add("item"); }
                if (s.Phase == "aim" && s.Cur == m) can.Add("aim");
                if (s.Phase == "prompt" && s.Pr?.Who == m) can.Add("pick");
                if (s.Phase == "pick" && s.Pk is { Chosen: null } pk && pk.Chooser == m) can.Add("pick");
                if (s.Phase == "late" && s.L is { } l && l.Choosers.Contains(m) && !l.Chosen.ContainsKey(m)) can.Add("pick");
                if (s.Phase == "card" && s.M is { } mm && mm.Seats.Contains(m) && !mm.Ready.Contains(m)) can.Add("ready");
                if (s.Phase == "card" && s.M is { Duel: [var a, var b] } && m != a && m != b) can.Add("bet");
            }
            if (mg && s.M is { } m2 && m2.Seats.Contains(m) && !g.Watching(m)) can.Add("mg");
            if (s.P[m].Auto) can.Add("here");
            if (!s.Done) can.Add("emo");
            var ferry = s.P[m].Items.Contains("key") || s.P[m].Coins >= 10;
            you = new
            {
                i = m, can,
                toStand = c.Map.Forward(s.P[m].Pos, s.Stand, ferry),
                tip = !s.P[m].Moved && s.Phase == "turn" && s.Cur == m,
            };
        }

        object? mgView = null;
        if (s.M is { } mgs && s.Phase is "pick" or "card" or "mg" or "results")
        {
            var running = mg && mgs.Running;
            var mine = me is { } mi && !g.Watching(mi) ? Array.IndexOf(mgs.Seats, mi) : -1;
            mgView = new
            {
                id = mgs.Id, title = mgs.Title, howto = mgs.Howto, repeat = mgs.Repeat, x2 = mgs.X2,
                duel = mgs.Duel, stake = mgs.Stake, honor = mgs.Honor,
                bets = mgs.Bets.GroupBy(kv => kv.Value).ToDictionary(gr => gr.Key.ToString(), gr => gr.Select(kv => kv.Key).Order().ToArray()),
                ready = mgs.Ready, seats = mgs.Seats,
                names = mgs.Seats.Select(k => s.P[k].Away ? "🤖 за " + s.P[k].Name : s.P[k].Name).ToArray(),
                nicks = mgs.Seats.Select(k => s.P[k].Bot || s.P[k].Away ? null : g.NickAt(k)).ToArray(),
                seatNames = running ? c.Mg.SeatNames() : null,
                sub = mine >= 0 ? mine : (int?)null,
                status = running ? "playing" : mgs.Results is null ? null : "finished",
                view = running ? c.Mg.View(mine >= 0 ? mgs.Seats[mine] : null) : null,
                results = mgs.Results?.Select(r => new { i = r.I, place = r.Place, coins = r.Coins }),
                how = mgs.How,
            };
        }

        return new
        {
            phase = s.Phase, round = s.Round, rounds = s.Rounds, target = s.Len, late = s.Late,
            lastGame = s.Round == s.Rounds && s.Rounds > 0,
            paused = s.Paused,
            map = s.Map, mapV = s.MapV,
            until = s.Paused is null ? s.Until?.ToString("O") : null,
            total = s.Until is null ? (int?)null : s.Total,
            players,
            cur = s.Cur,
            stand = s.Stand, price = c.Price, bank = s.Bank, duelDone = s.DuelDone,
            gates = mg ? null : s.Gates.Select(x => new { node = x.Node, owner = x.Owner }),
            shops = mg ? null : s.Shops,
            prompt = s.Phase == "prompt" && s.Pr is { } pr
                ? new
                {
                    who = pr.Who, kind = pr.Kind, node = pr.Node,
                    options = pr.Options.Select(o => new { k = o.K, label = o.Label, price = o.Price, ok = o.Ok }),
                }
                : null,
            aim = s.Phase == "aim" && s.Am is { } am
                ? new { who = s.Cur, item = am.Item, targets = am.Targets, nodes = am.Nodes, range = am.Range }
                : null,
            anim = s.Anim is { } an
                ? new { seq = an.Seq, kind = an.Kind, who = an.Who, path = an.Path, dice = an.Dice, key = an.Key, to = an.To, at = an.At.ToString("O"), ms = an.Ms }
                : null,
            fx = mg ? null : s.Fx.Select(f => new { seq = f.Seq, kind = f.Kind, who = f.Who, d = f.D, k = f.K }),
            emo = g.Emo.Select(e => new { seq = e.Seq, i = e.I, k = e.K }),
            pick = s.Pk is { } pk2 ? new { chooser = pk2.Chooser, options = pk2.Options, chosen = pk2.Chosen, duel = pk2.Duel } : null,
            mg = mgView,
            lateInfo = s.Phase == "late" && s.L is { } li
                ? new { choosers = li.Choosers, chosen = li.Chosen.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), options = VechirkaCore.LateOptions.Select(o => new { k = o.K, label = o.Label }) }
                : null,
            final = s.F is { } f && s.Phase is "final" or "done"
                ? new
                {
                    step = f.Step,
                    // номінації відкриваються по одній: ще не оголошені — не показуємо
                    bonuses = f.Bonuses.Take(Math.Max(0, f.Step + 1)).Select(b => new { key = b.Key, title = b.Title, winners = b.Winners }),
                    ranking = f.Ranking.Select(r => new { i = r[0], place = r[1] }),
                }
                : null,
            say = g.LastSay is { } sy ? new { id = sy.Id, text = sy.Text, url = sy.Url } : null,
            log = mg ? null : s.Log,
            you,
        };
    }
}
