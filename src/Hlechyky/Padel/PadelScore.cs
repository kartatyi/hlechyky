using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Padel;

// Рушій рахунку падела — перенесено з макета (_padel/padel.html: step / winGame / winSet / hints / ptsLabel / speak),
// разом із виправленою зміною сторін. Чистий: ні бази, ні часу, ні випадковості — стан однозначно виходить із правил,
// першого порядку подачі й журналу дій, тож «скасувати» = переграти журнал без останнього запису.

/// <summary>
/// Правила матчу. Mode: "match" (сети) чи "points" (американо-очки); Deuce: "adv" | "golden" | "star";
/// Sets: "1" | "3" | "3s" (третій — супертайбрейк) | "free" (без кінця); SetTo: 6 чи 4; Total — очок у points
/// (null — американо на час, кінця за очками нема).
/// </summary>
public sealed record PadelRules(string Mode = "match", string Deuce = "adv", string Sets = "1", int SetTo = 6, int? Total = null)
{
    public bool Points => Mode == "points";

    /// <summary>Що не так із правилами — людським текстом, або null.</summary>
    public string? Problem()
    {
        if (Mode is not ("match" or "points")) return "Невідомий режим рахунку";
        if (Deuce is not ("adv" or "golden" or "star")) return "Невідоме правило «рівно»";
        if (Sets is not ("1" or "3" or "3s" or "free")) return "Невідома кількість сетів";
        if (SetTo is not (6 or 4)) return "Сет — до 6 або до 4 геймів";
        if (Total is { } t && (t < 4 || t > 99)) return "Очок у матчі — від 4 до 99";
        return null;
    }

    /// <summary>Порожнє з клієнта → за замовчуванням (рішення власника: більше-менше, 1 сет, до 6).</summary>
    public static PadelRules From(PadelRules? r) => r is null ? new() : new(
        string.IsNullOrWhiteSpace(r.Mode) ? "match" : r.Mode, string.IsNullOrWhiteSpace(r.Deuce) ? "adv" : r.Deuce,
        string.IsNullOrWhiteSpace(r.Sets) ? "1" : r.Sets, r.SetTo == 0 ? 6 : r.SetTo, r.Mode == "points" ? r.Total : null);
}

/// <summary>Зіграний сет: гейми; <see cref="Tb"/> — очки тайбрейку, якщо був; <see cref="Stb"/> — це супертайбрейк (G — очки).</summary>
public sealed record PadelSet(int[] G, int[]? Tb, bool Stb);

/// <summary>Подія кроку: game{t,brk}, set{t}, match{t}, tb, stb, deuce{n,decisive}, decided, ends, serve.</summary>
public sealed record PadelEvent(string K, int T = -1, bool Brk = false, int N = 0, bool Decisive = false);

/// <summary>Стан рахунку (як <c>M</c> макета). Змінний — рушій крокує по ньому; копія — <see cref="Clone"/>.</summary>
public sealed class PadelState
{
    public int[] Pts = [0, 0];
    public int[] Games = [0, 0];
    public List<PadelSet> Sets = [];
    public int[] Won = [0, 0];
    public bool Tb;
    public int TbTo = 7;
    public bool SuperTb;
    public int Deuces;
    public string[] Order = ["A0", "B0", "A1", "B1"];
    public int Srv;
    public int TbSrv0;
    public int TotalGames;
    public bool Over;
    public int Winner = -1;
    /// <summary>Хто взяв кожне очко — хвиля матчу.</summary>
    public List<int> Log = [];

    public PadelState Clone() => new()
    {
        Pts = [.. Pts], Games = [.. Games], Sets = [.. Sets], Won = [.. Won], Tb = Tb, TbTo = TbTo, SuperTb = SuperTb,
        Deuces = Deuces, Order = [.. Order], Srv = Srv, TbSrv0 = TbSrv0, TotalGames = TotalGames, Over = Over,
        Winner = Winner, Log = [.. Log],
    };

    public string ServerSlot => Order[Srv];
    public int ServerTeam => PadelScore.TeamOf(Order[Srv]);
}

/// <summary>Підсумок переграного журналу: стан, події останнього запису, факти й статистика матчу.</summary>
public sealed record PadelReplay(PadelState State, List<PadelEvent> LastEvents, PadelFacts Facts, PadelStats Stats);

public sealed record PadelServeStat(int Won, int Of);

/// <summary>Статистика матчу з журналу: подача/прийом, найдовша серія очок, брейки, вирішальні, хвиля (A − B наростом).</summary>
public sealed record PadelStats(PadelServeStat[] Serve, PadelServeStat[] Recv, int[] Streak, int[] Breaks, int[] Golden, int[] Momentum);

public static class PadelScore
{
    public static readonly string[] Slots = ["A0", "B0", "A1", "B1"];
    public static int TeamOf(string slot) => slot[0] == 'A' ? 0 : 1;

    static int DecLimit(PadelRules r) => r.Deuce switch { "golden" => 1, "star" => 3, _ => int.MaxValue };

    /// <summary>Наступне очко вирішальне (золоте / star point).</summary>
    public static bool IsDecider(PadelState m, PadelRules r) =>
        !r.Points && !m.Tb && m.Pts[0] == m.Pts[1] && m.Pts[0] >= 3 && m.Deuces >= DecLimit(r);

    public static PadelState Fresh(string? first = null)
    {
        var m = new PadelState();
        if (first is not null) SetServer(m, new PadelRules(), first);
        return m;
    }

    /// <summary>Очко команді <paramref name="t"/>. Повертає події (порожньо, якщо матч уже скінчено).</summary>
    public static List<PadelEvent> Step(PadelState m, int t, PadelRules r)
    {
        var ev = new List<PadelEvent>();
        var o = 1 - t;
        if (m.Over) return ev;
        m.Log.Add(t);
        if (r.Points)
        {
            m.Pts[t]++;
            var n = m.Pts[0] + m.Pts[1];
            if (r.Total is { } total && n >= total)
            {
                m.Over = true;
                m.Winner = m.Pts[0] == m.Pts[1] ? -1 : m.Pts[0] > m.Pts[1] ? 0 : 1;
                ev.Add(new("match", m.Winner));
                return ev;
            }
            if (n % 4 == 0) { m.Srv = (m.Srv + 1) % 4; ev.Add(new("serve")); }
            return ev;
        }
        if (m.Tb)
        {
            m.Pts[t]++;
            var n = m.Pts[0] + m.Pts[1];
            if (m.Pts[t] >= m.TbTo && m.Pts[t] - m.Pts[o] >= 2) { WinSet(m, t, r, ev); return ev; }
            // Подача в тайбрейку: після першого очка, далі кожні два
            if (n % 2 == 1) m.Srv = (m.Srv + 1) % 4;
            if (n % 6 == 0) ev.Add(new("ends"));
            return ev;
        }
        if (IsDecider(m, r)) { ev.Add(new("decided", t)); WinGame(m, t, r, ev); return ev; }
        m.Pts[t]++;
        if (m.Pts[t] >= 4 && m.Pts[t] - m.Pts[o] >= 2) { WinGame(m, t, r, ev); return ev; }
        if (m.Pts[0] == m.Pts[1] && m.Pts[0] >= 3)
        {
            m.Pts = [3, 3];
            m.Deuces++;
            ev.Add(new("deuce", N: m.Deuces, Decisive: m.Deuces >= DecLimit(r)));
        }
        return ev;
    }

    static void WinGame(PadelState m, int t, PadelRules r, List<PadelEvent> ev)
    {
        var o = 1 - t;
        var brk = TeamOf(m.Order[m.Srv]) != t;
        m.Games[t]++; m.Pts = [0, 0]; m.Deuces = 0; m.TotalGames++; m.Srv = (m.Srv + 1) % 4;
        ev.Add(new("game", t, brk));
        var to = r.SetTo;
        // Сторони міняються після 1-го, 3-го, 5-го… гейму кожного сету (а не за наскрізним лічильником)
        if (m.Games[t] >= to && m.Games[t] - m.Games[o] >= 2) WinSet(m, t, r, ev);
        else if (m.Games[0] == to && m.Games[1] == to) { m.Tb = true; m.TbTo = 7; m.TbSrv0 = m.Srv; ev.Add(new("tb")); }
        else if ((m.Games[0] + m.Games[1]) % 2 == 1) ev.Add(new("ends"));
    }

    static void WinSet(PadelState m, int t, PadelRules r, List<PadelEvent> ev)
    {
        var wasTb = m.Tb;
        var stb = m.SuperTb;
        if (stb) m.Sets.Add(new([.. m.Pts], null, true));
        else
        {
            if (wasTb) m.Games[t]++;
            m.Sets.Add(new([.. m.Games], wasTb ? [.. m.Pts] : null, false));
        }
        // Тайбрейк — один гейм: 7:6 — непарно, сторони міняються й наприкінці сету
        var setOdd = !stb && (m.Games[0] + m.Games[1]) % 2 == 1;
        m.Won[t]++;
        if (wasTb) { m.Srv = (m.TbSrv0 + 1) % 4; m.TotalGames++; }
        m.Pts = [0, 0]; m.Games = [0, 0]; m.Tb = false; m.SuperTb = false; m.Deuces = 0;
        var need = r.Sets switch { "1" => 1, "free" => int.MaxValue, _ => 2 };
        if (m.Won[t] >= need) { m.Over = true; m.Winner = t; ev.Add(new("match", t)); return; }
        ev.Add(new("set", t));
        if (r.Sets == "3s" && m.Won[0] == 1 && m.Won[1] == 1)
        {
            m.Tb = true; m.SuperTb = true; m.TbTo = 10; m.TbSrv0 = m.Srv;
            ev.Add(new("stb"));
        }
        if (setOdd) ev.Add(new("ends"));
    }

    /// <summary>Чи можна зараз міняти подавача: початок гейму (не тайбрейк) чи між четвірками в points.</summary>
    public static bool ServeFree(PadelState m, PadelRules r)
    {
        var np = m.Pts[0] + m.Pts[1];
        return !m.Over && (r.Points ? np % 4 == 0 : np == 0 && !m.Tb);
    }

    /// <summary>
    /// Подає <paramref name="slot"/>; решта черги — по колу, команди чергуються, партнер подає через одного.
    /// null — гаразд; інакше текст відмови.
    /// </summary>
    public static string? SetServer(PadelState m, PadelRules r, string slot)
    {
        if (Array.IndexOf(Slots, slot) < 0) return "Невідомий гравець";
        if (m.Over) return "Матч уже скінчено";
        if (!ServeFree(m, r)) return "Подавача можна поміняти лише на початку гейму";
        if (m.Order[m.Srv] == slot) return null;
        static string Partner(string x) => $"{x[0]}{(x[1] == '0' ? '1' : '0')}";
        var other = slot[0] == 'A' ? 'B' : 'A';
        string nx = "";
        for (var i = 0; i < 4; i++) { var x = m.Order[(m.Srv + i) % 4]; if (x[0] == other) { nx = x; break; } }
        var o = new string[4];
        o[m.Srv] = slot; o[(m.Srv + 2) % 4] = Partner(slot); o[(m.Srv + 1) % 4] = nx; o[(m.Srv + 3) % 4] = Partner(nx);
        m.Order = o;
        return null;
    }

    /// <summary>
    /// Завершити вручну: переможець — більше сетів, далі геймів (усіх), у points — більше очок; нічия можлива.
    /// Недограний сет лягає в рахунок як є.
    /// </summary>
    public static void Finish(PadelState m, PadelRules r)
    {
        if (m.Over) return;
        m.Over = true;
        if (r.Points) { m.Winner = Cmp(m.Pts[0], m.Pts[1]); return; }
        if (m.SuperTb) { if (m.Pts[0] + m.Pts[1] > 0) m.Sets.Add(new([.. m.Pts], null, true)); }
        else if (m.Games[0] + m.Games[1] + m.Pts[0] + m.Pts[1] > 0 && (m.Games[0] + m.Games[1] > 0 || m.Tb))
            m.Sets.Add(new([.. m.Games], m.Tb ? [.. m.Pts] : null, false));
        var won = m.Won[0] != m.Won[1] ? Cmp(m.Won[0], m.Won[1]) : -1;
        if (won < 0)
        {
            var g = new int[2];
            foreach (var s in m.Sets.Where(s => !s.Stb)) { g[0] += s.G[0]; g[1] += s.G[1]; }
            won = Cmp(g[0], g[1]);
        }
        m.Winner = won;
        m.Pts = [0, 0]; m.Games = [0, 0]; m.Tb = false; m.SuperTb = false;
    }

    static int Cmp(int a, int b) => a == b ? -1 : a > b ? 0 : 1;

    /// <summary>Готовий напис очок команди: 0/15/30/40/AD або число (points, тайбрейк).</summary>
    public static string Label(PadelState m, PadelRules r, int t)
    {
        if (r.Points || m.Tb) return m.Pts[t].ToString();
        int a = m.Pts[t], b = m.Pts[1 - t];
        if (a >= 3 && b >= 3) return a > b ? "AD" : "40";
        return new[] { "0", "15", "30", "40" }[Math.Min(a, 3)];
    }

    /// <summary>Підказка над табло: текст і команда (-1 — нічия сторона), або null.</summary>
    public static (string Text, int Team)? Hint(PadelState m, PadelRules r)
    {
        if (m.Over) return null;
        if (r.Points)
            return r.Total is { } total && m.Pts[0] + m.Pts[1] == total - 1 ? ("Останній розіграш", -1) : null;
        var sim = new (PadelState C, List<PadelEvent> Ev)[2];
        for (var t = 0; t < 2; t++) { var c = m.Clone(); sim[t] = (c, Step(c, t, r)); }
        for (var t = 0; t < 2; t++) if (sim[t].C.Over && sim[t].C.Winner == t) return ("Матч-бол", t);
        for (var t = 0; t < 2; t++) if (sim[t].Ev.Any(e => e.K == "set")) return ("Сет-бол", t);
        // Вирішальне — важливіше за брейк-пойнт: на ньому брейк-пойнт у приймаючих завжди, а «золоте» — новина
        if (IsDecider(m, r)) return (r.Deuce == "star" ? "⭐ Star point" : "Золоте очко", -1);
        for (var t = 0; t < 2; t++) if (sim[t].Ev.Any(e => e.K == "game" && e.Brk)) return ("Брейк-пойнт", t);
        if (m.Tb) return (m.SuperTb ? "Супертайбрейк" : "Тайбрейк", -1);
        return null;
    }

    /// <summary>Бік подачі: парна кількість розіграних у геймі очок — справа, непарна — зліва; вирішальне — обирають приймаючі.</summary>
    public static string Side(PadelState m, PadelRules r)
    {
        if (IsDecider(m, r)) return "choice";
        return (m.Pts[0] + m.Pts[1]) % 2 == 0 ? "right" : "left";
    }

    /// <summary>Рахунок рядком для лобі й списків: «6:4 · 3:2 · 30:15».</summary>
    public static string ScoreText(PadelState m, PadelRules r)
    {
        if (r.Points) return $"{m.Pts[0]}:{m.Pts[1]}";
        var parts = m.Sets.Select(s => $"{s.G[0]}:{s.G[1]}").ToList();
        if (!m.Over)
        {
            if (!m.SuperTb) parts.Add($"{m.Games[0]}:{m.Games[1]}");
            parts.Add($"{Label(m, r, 0)}:{Label(m, r, 1)}");
        }
        return string.Join(" · ", parts);
    }

    static readonly string[] Words = ["нуль", "п’ятнадцять", "тридцять", "сорок"];

    /// <summary>
    /// Що каже Глек після кроку (як <c>speak()</c> макета): рахунок з боку подавача, «Рівно», «Перевага: …», гейм, сет,
    /// матч. <paramref name="team"/> — «Влад і Микола» для команди.
    /// </summary>
    public static string Speak(PadelState m, PadelRules r, IReadOnlyList<PadelEvent> ev, Func<int, string> team)
    {
        PadelEvent? Has(string k) => ev.FirstOrDefault(e => e.K == k);
        if (Has("match") is { } mt) return mt.T < 0 ? "Нічия!" : $"Матч! Перемогли {team(mt.T)}";
        if (Has("set") is { } st) return $"Сет! {team(st.T)}";
        if (Has("game") is { } g) return $"Гейм, {team(g.T)}. {m.Games[0]} — {m.Games[1]}" + (Has("ends") is not null ? ". Зміна сторін" : "");
        var s = m.ServerTeam;
        if (r.Points || m.Tb) return $"{m.Pts[s]} — {m.Pts[1 - s]}";
        if (Has("deuce") is { } d) return d.Decisive ? (r.Deuce == "star" ? "Рівно. Вирішальне очко" : "Рівно. Золоте очко") : "Рівно";
        if (m.Pts[0] >= 3 && m.Pts[1] >= 3 && m.Pts[0] != m.Pts[1]) return $"Перевага: {team(m.Pts[0] > m.Pts[1] ? 0 : 1)}";
        return $"{Words[Math.Min(3, m.Pts[s])]} — {Words[Math.Min(3, m.Pts[1 - s])]}";
    }

    /// <summary>Усі «x — y» звичайного гейму й службові фрази — їх варто озвучити ще на старті сервера.</summary>
    public static IEnumerable<string> CommonPhrases()
    {
        foreach (var a in Words) foreach (var b in Words) yield return $"{a} — {b}";
        for (var a = 0; a <= 10; a++) for (var b = 0; b <= 10; b++) yield return $"{a} — {b}";
        yield return "Рівно";
        yield return "Рівно. Золоте очко";
        yield return "Рівно. Вирішальне очко";
        yield return "Нічия!";
    }

    /// <summary>
    /// Переграти журнал: "0"/"1" — очко, "s:A1" — подає A1, "f" — завершено вручну. Хибні записи пропускаються
    /// (журнал пишемо лише ми, але стара база не мусить валити сервер).
    /// </summary>
    public static PadelReplay Replay(PadelRules r, string[] firstOrder, IEnumerable<string> journal)
    {
        var m = new PadelState { Order = [.. firstOrder] };
        var last = new List<PadelEvent>();
        int[] golden = [0, 0], tbs = [0, 0], streak = [0, 0], breaks = [0, 0];
        bool[] bagel = [false, false], comeback = [false, false], trailed = [false, false];
        var serve = new int[2, 2]; // [команда, 0 — розіграно / 1 — виграно]
        var momentum = new List<int>();
        int run = 0, runTeam = -1, diff = 0;
        int lowG = r.SetTo == 6 ? 1 : 0, highG = r.SetTo == 6 ? 5 : 3;
        foreach (var e in journal)
        {
            if (e is "0" or "1")
            {
                if (m.Over) continue;
                var t = e[0] - '0';
                if (!r.Points && !m.Tb)
                    for (var x = 0; x < 2; x++)
                        if (m.Games[x] <= lowG && m.Games[1 - x] >= highG) trailed[x] = true;
                var s = m.ServerTeam;
                serve[s, 0]++;
                if (t == s) serve[s, 1]++;
                var wasTb = m.Tb;
                last = Step(m, t, r);
                diff += t == 0 ? 1 : -1;
                momentum.Add(diff);
                if (runTeam == t) run++; else { run = 1; runTeam = t; }
                streak[t] = Math.Max(streak[t], run);
                foreach (var ev in last)
                {
                    if (ev.K == "decided") golden[ev.T]++;
                    if (ev.K == "game" && ev.Brk) breaks[ev.T]++;
                    if (ev.K is "set" or "match" && ev.T >= 0 && !r.Points)
                    {
                        if (wasTb) tbs[ev.T]++;
                        var set = m.Sets[^1];
                        if (!set.Stb && set.Tb is null && set.G[1 - ev.T] == 0 && set.G[ev.T] >= r.SetTo) bagel[ev.T] = true;
                        if (!set.Stb && trailed[ev.T]) comeback[ev.T] = true;
                        trailed = [false, false];
                    }
                }
            }
            else if (e.StartsWith("s:", StringComparison.Ordinal))
            {
                SetServer(m, r, e[2..]);
                last = [];
            }
            else if (e == "f")
            {
                Finish(m, r);
                last = [new("match", m.Winner)];
            }
        }
        var stats = new PadelStats(
            [new(serve[0, 1], serve[0, 0]), new(serve[1, 1], serve[1, 0])],
            [new(serve[1, 0] - serve[1, 1], serve[1, 0]), new(serve[0, 0] - serve[0, 1], serve[0, 0])],
            streak, breaks, golden, [.. momentum]);
        return new(m, last, new PadelFacts(golden, tbs, bagel, comeback), stats);
    }

    /// <summary>id голосового кліпу: перші 16 hex SHA1 тексту.</summary>
    public static string VoiceId(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.Trim()))).ToLowerInvariant()[..16];
}
