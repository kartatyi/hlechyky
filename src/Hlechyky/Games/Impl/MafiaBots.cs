using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// 🤖 Селяни-боти (specs/mafia.md «Прохід №3»): компанія на трьох-п'ятьох добирає стіл до справжньої мафії.
/// Агенти MCP за стіл самі не сідають — їх треба запускати ззовні, тож кнопка «🤖 Додати гравця» садить простих
/// вбудованих ботів. Вони мовчать, ходять обережно й навмання (ролі розкусити не вміють), але тримають склад:
/// мафії є кого різати, мирним — з ким рахуватись. Черепків, ачівок і рядків у таблицях боти не отримують.
/// <para>
/// Боти сидять на вільних місцях столу, яких каркас не бачить (<c>Ctx.Seated</c> = false): каркас не дає їм ні
/// перемог, ні балачки. Ходи роблять тим самим кодом, що й люди (<see cref="Act"/>-гілки), з тика — у випадкову
/// мить фази, щоб з таймінгу нічого не вгадати.
/// </para>
/// </summary>
public sealed partial class Mafia
{
    /// <summary>Імена ботів — по черзі. Емодзі на початку — щоб ніхто не сплутав бота з кумом Петром.</summary>
    public static readonly string[] BotNames =
    [
        "🤖 Дід Панас", "🤖 Баба Параска", "🤖 Кум Опанас", "🤖 Тітка Мотря", "🤖 Сват Грицько", "🤖 Одарка",
        "🤖 Дядько Омелько", "🤖 Солоха", "🤖 Писар Хома", "🤖 Ковалиха", "🤖 Пасічник Рудий",
    ];

    /// <summary>Скільки ботів хоче стіл (лобі). Справді сядуть — скільки влізе на старті.</summary>
    int _botWanted;
    /// <summary>Хто з місць партії — бот.</summary>
    readonly HashSet<int> _bots = [];
    /// <summary>Коли бот ходить у цій фазі (перший і другий раз — удень бот ще передумує за селом).</summary>
    readonly Dictionary<int, (DateTimeOffset First, DateTimeOffset Second, int Done)> _botPlan = [];

    public override bool ActsInLobby => true;

    public bool IsBot(int seat) => _bots.Contains(seat);
    public int BotsWanted => _botWanted;

    /// <summary>Скільки людей сидить за столом зараз.</summary>
    int Humans()
    {
        var n = 0;
        for (var i = 0; i < Info.MaxPlayers; i++) if (Ctx.Seated(i)) n++;
        return n;
    }

    public override string? CanStart() =>
        Humans() + Math.Min(_botWanted, Info.MaxPlayers - Humans()) >= TrioSize ? null
            : "Треба щонайменше троє — поклич друга або тисни «🤖 Додати гравця»";

    /// <summary>Лобі: «🤖 Додати гравця» / «прибрати бота». Будь-хто за столом — свої ж люди.</summary>
    ActResult LobbyAct(string action, JsonElement payload)
    {
        if (action != "bots") return ActResult.Fail("Партія ще не почалась");
        var n = payload.ValueKind == JsonValueKind.Number && payload.TryGetInt32(out var v) ? v
            : payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("n", out var e) && e.TryGetInt32(out var w) ? w : -1;
        var room = Info.MaxPlayers - Humans();
        if (n < 0 || n > Info.MaxPlayers - 1) return ActResult.Fail("Стільки ботів не буває");
        if (n > _botWanted && n > room) return ActResult.Fail("За столом уже нема вільних лав");
        _botWanted = n;
        return ActResult.Done;
    }

    /// <summary>Посадити ботів на вільні місця (після людей). Кличеться зі <see cref="Start"/> до роздачі.</summary>
    int[] SeatBots(int[] humans)
    {
        _bots.Clear();
        _botPlan.Clear();
        var list = new List<int>(humans);
        var k = 0;
        for (var i = 0; i < Info.MaxPlayers && k < _botWanted; i++)
        {
            if (Ctx.Seated(i)) continue;
            _bots.Add(i);
            _nicks[i] = BotNames[k % BotNames.Length];
            list.Add(i);
            k++;
        }
        list.Sort();
        return [.. list];
    }

    /// <summary>Місця ботів, як вони сядуть, — для картки лобі (ще до старту).</summary>
    IEnumerable<(int Seat, string Nick)> LobbyBots()
    {
        var k = 0;
        for (var i = 0; i < Info.MaxPlayers && k < _botWanted; i++)
        {
            if (Ctx.Seated(i)) continue;
            yield return (i, BotNames[k % BotNames.Length]);
            k++;
        }
    }

    /// <summary>На вході у фазу: кожному живому ботові — коли ходити (випадкова мить, а вдень ще й «передумати»).</summary>
    void PlanBots()
    {
        _botPlan.Clear();
        if (_bots.Count == 0) return;
        var from = Ctx.Clock.UtcNow;
        var len = Length(_phase);
        foreach (var b in _bots)
        {
            if (_dead.Contains(b)) continue;
            if (_phase == MafiaPhase.Night)
                _botPlan[b] = (from.AddMilliseconds(len * (15 + Ctx.Rng.Next(45)) / 100), DateTimeOffset.MaxValue, 0);
            else if (_phase == MafiaPhase.Vote)
                _botPlan[b] = (from.AddMilliseconds(len * (30 + Ctx.Rng.Next(35)) / 100),
                    from.AddMilliseconds(len * (80 + Ctx.Rng.Next(10)) / 100), 0);
        }
    }

    /// <summary>Тик: боти, чия мить настала, роблять свою справу.</summary>
    void BotsAct()
    {
        if (_botPlan.Count == 0) return;
        var now = Ctx.Clock.UtcNow;
        foreach (var b in _bots)
        {
            if (!_botPlan.TryGetValue(b, out var p) || _dead.Contains(b)) continue;
            if (p.Done == 0 && now >= p.First)
            {
                BotMove(b, first: true);
                _botPlan[b] = (p.First, p.Second, 1);
            }
            else if (p.Done == 1 && now >= p.Second)
            {
                BotMove(b, first: false);
                _botPlan[b] = (p.First, p.Second, 2);
            }
        }
    }

    static JsonElement Seat(int? s) => JsonSerializer.SerializeToElement(s);

    void BotMove(int bot, bool first)
    {
        var role = _roles[bot];
        var alive = Alive().ToList();
        var others = alive.Where(s => s != bot).ToList();
        if (others.Count == 0) return;
        if (_phase == MafiaPhase.Night)
        {
            switch (role)
            {
                case MafiaRole.Mafia or MafiaRole.Don when !QuietNight:
                    // Команда важливіша за смак: хтось зі своїх уже показав — туди ж.
                    int? mate = null;
                    foreach (var (k, t) in _kill) if (k != bot && !_dead.Contains(k) && !_dead.Contains(t)) { mate = t; break; }
                    var prey = others.Where(s => !IsMafia(_roles[s])).ToList();
                    if (mate is null && prey.Count == 0) return;
                    Kill(bot, role, Seat(mate ?? prey[Ctx.Rng.Next(prey.Count)]));
                    break;
                case MafiaRole.Maniac when !QuietNight:
                    Kill(bot, role, Seat(others[Ctx.Rng.Next(others.Count)]));
                    break;
                case MafiaRole.Sheriff when !_checkedTonight:
                    var unknown = others.Where(s => !_checks.ContainsKey(s)).ToList();
                    if (unknown.Count > 0) Check(bot, role, Seat(unknown[Ctx.Rng.Next(unknown.Count)]));
                    break;
                case MafiaRole.Doctor when _heal is null:
                    var patients = alive.Where(s => s != _healedLast && (_selfHeal || s != bot)).ToList();
                    if (patients.Count > 0) Heal(bot, role, Seat(patients[Ctx.Rng.Next(patients.Count)]));
                    break;
                case MafiaRole.Kuma when _block is null:
                    var hosts = others.Where(s => s != _blockedLast).ToList();
                    if (hosts.Count > 0) Block(bot, role, Seat(hosts[Ctx.Rng.Next(hosts.Count)]));
                    break;
            }
            return;
        }
        if (_phase == MafiaPhase.Vote) Vote(bot, Seat(BotVote(bot, role, others, first)));
    }

    /// <summary>
    /// Обережний голос: бот не вигадує підозр, а йде за селом — за тим, проти кого вже найбільше рук. Комісар, що
    /// знайшов мафію, голосує проти неї; мафіозі своїх не топить і, бува, першим показує на мирного; решта хіба зрідка
    /// кидає перший камінь. Нема за ким іти — утримується.
    /// </summary>
    int? BotVote(int bot, MafiaRole role, List<int> others, bool first)
    {
        if (role == MafiaRole.Sheriff)
            foreach (var (s, isMafia) in _checks)
                if (isMafia && !_dead.Contains(s)) return s;

        var mafia = IsMafia(role);
        int? leader = null;
        var best = 0;
        foreach (var s in others)
        {
            if (mafia && IsMafia(_roles[s])) continue;
            var n = 0;
            foreach (var (voter, t) in _votes) if (t == s && voter != bot && !_dead.Contains(voter)) n++;
            if (n > best) { best = n; leader = s; }
        }
        if (leader is not null) return leader;
        if (!first) return _votes.TryGetValue(bot, out var mine) ? mine : null;
        var seed = mafia ? 45 : 15;
        if (Ctx.Rng.Next(100) >= seed) return null;
        var pool = others.Where(s => !mafia || !IsMafia(_roles[s])).ToList();
        return pool.Count > 0 ? pool[Ctx.Rng.Next(pool.Count)] : null;
    }
}
