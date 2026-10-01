using System.Globalization;
using System.Text.Json;
using Hlechyky.Games;
using Microsoft.Extensions.Options;
using static Hlechyky.Padel.PadelMoneySql;

namespace Hlechyky.Padel;

// =====================================================================================================================
// Гроші (контракт §3.2–3.3): хто кому скільки за корт. Зазвичай один платить за все, решта винні йому свою частку.
// Баланс — загальний між кожною парою людей (як Splitwise), без «спрощення через третіх»: кожен скидає тому, з ким
// грав і хто за нього платив. Гроші — цілі гривні.
// =====================================================================================================================

public sealed record PadelOtherLine(string Title, int Amount, string[]? Pids);

/// <summary>Що ввели у витрату (частки з цього рахуються щоразу — так правка однієї цифри не лишає старих часток).</summary>
public sealed record PadelExpenseData(string[] People, int Court, int RacketPrice, string[] Rackets, PadelOtherLine[] Other);

public sealed record PadelExpense(long Id, long? Gathering, string Date, string Payer, string ByPid, DateTimeOffset At, PadelExpenseData Data)
{
    public string Key => "e" + Id;
}

public sealed record PadelPayment(long Id, string From, string To, int Amount, DateTimeOffset At, string ByPid, string Note)
{
    public string Key => "p" + Id;
}

/// <summary>Банк у профілі: картка (16 цифр) і/або посилання (банка Monobank тощо).</summary>
public sealed record PadelBank(string Id, string Bank, string Title, string? Card, string? Link);

/// <summary>Сховище витрат, платежів і банків. DDL і SQL — тут.</summary>
public sealed class PadelMoneyStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS padel_expenses(
            id INTEGER PRIMARY KEY AUTOINCREMENT, gathering INTEGER, date TEXT NOT NULL, payer TEXT NOT NULL,
            by_pid TEXT NOT NULL, at TEXT NOT NULL, data TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS padel_expenses_gathering ON padel_expenses(gathering) WHERE gathering IS NOT NULL;
        CREATE TABLE IF NOT EXISTS padel_payments(
            id INTEGER PRIMARY KEY AUTOINCREMENT, from_pid TEXT NOT NULL, to_pid TEXT NOT NULL, amount INTEGER NOT NULL,
            at TEXT NOT NULL, by_pid TEXT NOT NULL, note TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS padel_banks(pid TEXT NOT NULL PRIMARY KEY, data TEXT NOT NULL, at TEXT NOT NULL);
        """;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const string ExpenseCols = "id, gathering, date, payer, by_pid, at, data";

    readonly Db _db;

    public PadelMoneyStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, Schema); });
    }

    static PadelExpense ExpenseRow(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1),
        r.GetString(2), r.GetString(3), r.GetString(4), Ts(r.GetString(5)), JsonSerializer.Deserialize<PadelExpenseData>(r.GetString(6), Json)!);

    /// <summary>Записати витрату; null — для цього збору вже є (унікальний індекс).</summary>
    public long? AddExpense(PadelExpense e) => _db.With(c =>
    {
        try
        {
            using var cmd = Cmd(c, $"INSERT INTO padel_expenses(gathering, date, payer, by_pid, at, data) VALUES($g, $d, $p, $b, $at, $data); SELECT last_insert_rowid();",
                ("$g", e.Gathering), ("$d", e.Date), ("$p", e.Payer), ("$b", e.ByPid), ("$at", Iso(e.At)), ("$data", JsonSerializer.Serialize(e.Data, Json)));
            return (long?)(long)cmd.ExecuteScalar()!;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) { return null; }   // CONSTRAINT
    });

    public bool UpdateExpense(PadelExpense e) => _db.With(c =>
    {
        try
        {
            return Exec(c, "UPDATE padel_expenses SET gathering=$g, date=$d, payer=$p, data=$data WHERE id=$id",
                ("$g", e.Gathering), ("$d", e.Date), ("$p", e.Payer), ("$data", JsonSerializer.Serialize(e.Data, Json)), ("$id", e.Id)) > 0;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    });

    public bool DeleteExpense(long id) => _db.With(c => Exec(c, "DELETE FROM padel_expenses WHERE id=$id", ("$id", id)) > 0);

    public PadelExpense? Expense(long id) => _db.With(c =>
        Rows(c, $"SELECT {ExpenseCols} FROM padel_expenses WHERE id=$id", ExpenseRow, ("$id", id)).FirstOrDefault());

    /// <summary>Id витрати збору або null.</summary>
    public long? ExpenseFor(long gathering) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT id FROM padel_expenses WHERE gathering=$g", ("$g", gathering));
        return cmd.ExecuteScalar() is long id ? (long?)id : null;
    });

    /// <summary>Усі витрати за часом запису (для балансу).</summary>
    public List<PadelExpense> Expenses() => _db.With(c => Rows(c, $"SELECT {ExpenseCols} FROM padel_expenses ORDER BY id", ExpenseRow));

    public long AddPayment(PadelPayment p) => _db.With(c =>
    {
        using var cmd = Cmd(c, "INSERT INTO padel_payments(from_pid, to_pid, amount, at, by_pid, note) VALUES($f, $t, $a, $at, $b, $n); SELECT last_insert_rowid();",
            ("$f", p.From), ("$t", p.To), ("$a", p.Amount), ("$at", Iso(p.At)), ("$b", p.ByPid), ("$n", p.Note));
        return (long)cmd.ExecuteScalar()!;
    });

    public PadelPayment? Payment(long id) => Payments("WHERE id=$id", ("$id", id)).FirstOrDefault();
    public List<PadelPayment> Payments() => Payments("");
    public bool DeletePayment(long id) => _db.With(c => Exec(c, "DELETE FROM padel_payments WHERE id=$id", ("$id", id)) > 0);

    List<PadelPayment> Payments(string where, params (string, object?)[] ps) => _db.With(c =>
        Rows(c, $"SELECT id, from_pid, to_pid, amount, at, by_pid, note FROM padel_payments {where} ORDER BY id", r =>
            new PadelPayment(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Ts(r.GetString(4)), r.GetString(5), r.GetString(6)), ps));

    public List<PadelBank> Banks(string pid) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT data FROM padel_banks WHERE pid=$p", ("$p", pid));
        return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<List<PadelBank>>(s, Json) ?? [] : [];
    });

    public void SetBanks(string pid, List<PadelBank> banks, DateTimeOffset at) => _db.With(c =>
        Exec(c, "INSERT INTO padel_banks(pid, data, at) VALUES($p, $d, $at) ON CONFLICT(pid) DO UPDATE SET data=excluded.data, at=excluded.at",
            ("$p", pid), ("$d", JsonSerializer.Serialize(banks, Json)), ("$at", Iso(at))));
}

/// <summary>Частки, баланс, платежі, банки.</summary>
public sealed class PadelMoney(PadelMoneyStore store, PadelGather gather, IPadelPlayers players, IPadelWire wire,
    IClock clock, IOptions<PadelOptions> options, ILogger<PadelMoney> log)
{
    public const int AmountMax = 100_000, TotalMax = 1_000_000, OtherMax = 10, BanksMax = 6, PeopleMax = 48;
    public static readonly string[] BankKinds = ["mono", "privat", "pumb", "abank", "sense", "izi", "other"];
    // Кілька правок поспіль (дві вкладки) не мусять записати дві витрати одного збору — унікальний індекс це тримає,
    // а замок — щоб перевірка прав і запис ішли разом.
    readonly object _gate = new();

    public sealed record OtherRequest(string? Title, int? Amount, string[]? Pids);
    public sealed record ExpenseRequest(string? Gathering, string? Date, string? Payer, string[]? People, int? Court,
        int? RacketPrice, string[]? Rackets, OtherRequest[]? Other);
    public sealed record PaymentRequest(string? From, string? To, int? Amount, string? Note);
    public sealed record BankRequest(string? Id, string? Bank, string? Title, string? Card, string? Link);

    // ---------------------------------------------------------------- частки

    /// <summary>
    /// Частки витрати: корт — порівну між people, ракетка — ціна кожному, хто брав, кожен рядок «іншого» — порівну
    /// між своїми (null — між people). Кожна частина ділиться цілими гривнями окремо; остача по 1 грн — першим за
    /// списком, платник — останнім (сума часток завжди рівно дорівнює сумі витрати).
    /// </summary>
    public static (int Total, Dictionary<string, int> Shares) Split(PadelExpenseData d, string payer)
    {
        var shares = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in d.People) shares.TryAdd(p, 0);
        var total = 0;
        void Part(int amount, IReadOnlyList<string> who)
        {
            if (amount <= 0 || who.Count == 0) return;
            total += amount;
            var order = who.Where(p => p != payer).Concat(who.Where(p => p == payer)).ToList();
            var each = amount / order.Count;
            var rest = amount % order.Count;
            for (var i = 0; i < order.Count; i++)
                shares[order[i]] = shares.GetValueOrDefault(order[i]) + each + (i < rest ? 1 : 0);
        }
        Part(d.Court, d.People);
        foreach (var r in d.Rackets) Part(d.RacketPrice, [r]);
        foreach (var o in d.Other) Part(o.Amount, o.Pids ?? d.People);
        return (total, shares);
    }

    /// <summary>Перевірити й дорахувати витрату з форми (порожні поля — зі збору й налаштувань). Fail — що не так.</summary>
    (PadelExpense? E, IResult? Fail) Resolve(PadelMoneyActor me, ExpenseRequest b, PadelExpense? old)
    {
        IResult Bad(string m) => PadelMoneyHttp.Fail(m);
        var o = options.Value;
        (PadelGathering G, List<PadelGoer> All)? g = null;
        var gid = b.Gathering ?? (old?.Gathering is { } og ? "g" + og : null);
        if (!string.IsNullOrWhiteSpace(gid))
        {
            g = gather.Load(gid.Trim());
            if (g is null) return (null, Bad("Нема такого збору"));
        }
        var going = g is { } x ? x.All.Take(x.G.Slots).ToList() : [];

        string[]? Pids(string[]? raw, string what)
        {
            if (raw is null) return null;
            var list = raw.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToArray();
            return list.All(players.Exists) ? list : throw new ArgumentException($"{what}: є невідомий гравець");
        }
        try
        {
            var people = Pids(b.People, "Хто ділить") ?? [.. going.Select(p => players.Canon(p.Pid)).Distinct()];
            if (people.Length == 0) return (null, Bad("Кого ділимо? Обери людей"));
            if (people.Length > PeopleMax) return (null, Bad($"Забагато людей — до {PeopleMax}"));
            var court = b.Court ?? (int)Math.Round(o.CourtPerHour * (g is { } y ? y.G.Hours * y.G.Courts : 1));
            if (court is < 0 or > TotalMax) return (null, Bad("Корт — від 0 до 1 000 000 грн"));
            var racketPrice = b.RacketPrice ?? o.RacketPrice;
            if (racketPrice is < 0 or > 10_000) return (null, Bad("Ракетка — від 0 до 10 000 грн"));
            var rackets = Pids(b.Rackets, "Ракетки") ?? [.. going.Where(p => p.Racket).Select(p => players.Canon(p.Pid)).Distinct()];
            if ((b.Other?.Length ?? 0) > OtherMax) return (null, Bad($"«Іншого» — до {OtherMax} рядків"));
            var other = new List<PadelOtherLine>();
            foreach (var line in b.Other ?? [])
            {
                var title = (line.Title ?? "").Trim();
                if (title.Length == 0) title = "Інше";
                if (title.Length > 40) return (null, Bad("Назва рядка «інше» — до 40 символів"));
                if (line.Amount is not { } amount || amount < 1 || amount > AmountMax) return (null, Bad("Сума рядка «інше» — від 1 до 100 000 грн"));
                var pids = Pids(line.Pids, title);
                if (pids is { Length: 0 }) return (null, Bad($"«{title}»: на кого ділимо?"));
                other.Add(new PadelOtherLine(title, amount, pids));
            }
            var payer = string.IsNullOrWhiteSpace(b.Payer) ? old?.Payer ?? me.Pid! : b.Payer.Trim();
            if (!players.Exists(payer)) return (null, Bad("Хто платив? Нема такого гравця"));
            var date = b.Date?.Trim();
            if (string.IsNullOrEmpty(date)) date = g is { } z ? z.G.Local[..10] : old?.Date ?? Days.Of(clock.UtcNow);
            else if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return (null, Bad("Дата — як 2026-10-04"));
            var data = new PadelExpenseData(people, court, racketPrice, rackets, [.. other]);
            var (total, _) = Split(data, payer);
            if (total < 1) return (null, Bad("Нема що ділити — сума нуль"));
            if (total > TotalMax) return (null, Bad("Завелика сума"));
            return (new PadelExpense(old?.Id ?? 0, g?.G.Id, date, payer, old?.ByPid ?? me.Pid!, old?.At ?? clock.UtcNow, data), null);
        }
        catch (ArgumentException ex) { return (null, Bad(ex.Message)); }
    }

    public object ExpenseView(PadelExpense e)
    {
        var (total, shares) = Split(e.Data, e.Payer);
        return new
        {
            id = e.Id == 0 ? null : e.Key,
            gathering = e.Gathering is { } g ? "g" + g : null,
            date = e.Date,
            payer = e.Payer,
            by = players.Name(e.ByPid),
            byPid = e.ByPid,
            at = e.At.UtcDateTime,
            people = e.Data.People,
            court = e.Data.Court,
            racketPrice = e.Data.RacketPrice,
            rackets = e.Data.Rackets,
            other = e.Data.Other.Select(o => new { title = o.Title, amount = o.Amount, pids = o.Pids }).ToList(),
            total,
            shares,
        };
    }

    // ---------------------------------------------------------------- витрати

    public IResult Preview(PadelMoneyActor me, ExpenseRequest b)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        var (e, fail) = Resolve(me, b, null);
        if (fail is not null) return fail;
        var (total, shares) = Split(e!.Data, e.Payer);
        // Окрім total/shares — уся дорахована витрата: форма бере з неї передзаповнення зі збору
        return PadelMoneyHttp.Ok(new { ok = true, total, shares, expense = ExpenseView(e) });
    }

    public IResult Create(PadelMoneyActor me, ExpenseRequest b)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        PadelExpense made;
        lock (_gate)
        {
            var (e, fail) = Resolve(me, b, null);
            if (fail is not null) return fail;
            if (e!.Gathering is { } g && store.ExpenseFor(g) is not null) return PadelMoneyHttp.Fail(OneForGathering);
            if (store.AddExpense(e) is not { } id) return PadelMoneyHttp.Fail(OneForGathering);
            made = e with { Id = id };
        }
        log.LogInformation("Падельня: витрата {Id} ({Total} грн)", made.Key, Split(made.Data, made.Payer).Total);
        Changed(made.Gathering);
        return PadelMoneyHttp.Ok(new { ok = true, expense = ExpenseView(made) });
    }

    const string OneForGathering = "Для цього збору вже є розрахунок — відредагуй його";

    public IResult Update(PadelMoneyActor me, string id, ExpenseRequest b)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        PadelExpense next;
        lock (_gate)
        {
            if (IdOf(id, 'e') is not { } n || store.Expense(n) is not { } old) return PadelMoneyHttp.Fail("Нема такого розрахунку", 404);
            if (!MayEdit(me, old)) return PadelMoneyHttp.Fail(EditRights, 403);
            var (e, fail) = Resolve(me, b, old);
            if (fail is not null) return fail;
            if (e!.Gathering is { } g && g != old.Gathering && store.ExpenseFor(g) is not null) return PadelMoneyHttp.Fail(OneForGathering);
            if (!store.UpdateExpense(e)) return PadelMoneyHttp.Fail(OneForGathering);
            next = e;
            if (old.Gathering != e.Gathering) Changed(old.Gathering, money: false);
        }
        Changed(next.Gathering);
        return PadelMoneyHttp.Ok(new { ok = true, expense = ExpenseView(next) });
    }

    public IResult Delete(PadelMoneyActor me, string id)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        PadelExpense old;
        lock (_gate)
        {
            if (IdOf(id, 'e') is not { } n || store.Expense(n) is not { } e) return PadelMoneyHttp.Fail("Нема такого розрахунку", 404);
            if (!MayEdit(me, e)) return PadelMoneyHttp.Fail(EditRights, 403);
            store.DeleteExpense(n);
            old = e;
        }
        Changed(old.Gathering);
        return PadelMoneyHttp.Ok(new { ok = true });
    }

    const string EditRights = "Правити розрахунок може той, хто його записав, платник або адмін";

    bool MayEdit(PadelMoneyActor me, PadelExpense e) => me.Admin || e.ByPid == me.Pid || players.Canon(e.Payer) == me.Pid;

    /// <summary>Пінг грошей; якщо витрата прив'язана до збору — і вид збору (у ньому видно, що розрахунок є).</summary>
    void Changed(long? gathering, bool money = true)
    {
        if (money) wire.Money();
        if (gathering is { } g && gather.Load("g" + g) is { } x) wire.Gathering(gather.View(x.G, x.All));
    }

    // ---------------------------------------------------------------- платежі

    public IResult Pay(PadelMoneyActor me, PaymentRequest b)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        var from = b.From?.Trim() ?? "";
        var to = b.To?.Trim() ?? "";
        if (!players.Exists(from) || !players.Exists(to)) return PadelMoneyHttp.Fail("Нема такого гравця");
        var cf = players.Canon(from);
        var ct = players.Canon(to);
        if (cf == ct) return PadelMoneyHttp.Fail("Сам собі не скинеш");
        if (b.Amount is not { } amount || amount < 1 || amount > AmountMax) return PadelMoneyHttp.Fail("Сума — від 1 до 100 000 грн");
        var note = (b.Note ?? "").Trim();
        if (note.Length > 100) return PadelMoneyHttp.Fail("Примітка — до 100 символів");
        // За гостя записує його контрагент: «Петро віддав мені готівкою» — і це вже покриває правило «from або to»
        if (!me.Admin && me.Pid != cf && me.Pid != ct) return PadelMoneyHttp.Fail("Платіж записує той, хто скинув, або той, хто отримав", 403);
        var id = store.AddPayment(new PadelPayment(0, from, to, amount, clock.UtcNow, me.Pid!, note));
        var who = players.Name(me.Pid!);
        // Без роду (не знаємо, хто скинув чи скинула): «Від Олі: скинуто тобі…» / «Влад: від тебе отримано…»
        if (me.Pid == cf) { if (Pid.IsUser(ct)) wire.Toast(Pid.NickKey(ct)!, $"💸 Від {NickCases.Genitive(who)}: скинуто тобі {amount} грн"); }
        else if (me.Pid == ct) { if (Pid.IsUser(cf)) wire.Toast(Pid.NickKey(cf)!, $"💸 {who}: від тебе отримано {amount} грн"); }
        else
            foreach (var p in new[] { cf, ct }.Where(Pid.IsUser))
                wire.Toast(Pid.NickKey(p)!, $"💸 {who} записує: {players.Name(from)} → {players.Name(to)}, {amount} грн");
        wire.Money();
        return PadelMoneyHttp.Ok(new { ok = true, id = "p" + id });
    }

    public IResult Unpay(PadelMoneyActor me, string id)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        if (IdOf(id, 'p') is not { } n || store.Payment(n) is not { } p) return PadelMoneyHttp.Fail("Нема такого платежу", 404);
        if (!me.Admin && p.ByPid != me.Pid) return PadelMoneyHttp.Fail("Прибрати платіж може той, хто його записав, або адмін", 403);
        store.DeletePayment(n);
        wire.Money();
        return PadelMoneyHttp.Ok(new { ok = true });
    }

    // ---------------------------------------------------------------- баланс

    /// <summary>
    /// Увесь граф боргів: для кожної пари людей — одне число (частки, які один винен іншому як платнику, мінус
    /// платежі, в обидва боки). Канонічні pid: прив'язаний гість рахується своєму акаунту. Лише ненульові, більші згори.
    /// </summary>
    public List<(string From, string To, int Amount)> Ledger() => Ledger(null);

    /// <summary>
    /// Граф боргів без записів, які зробив <paramref name="author"/> (витрати з ByPid і платежі, записані ним):
    /// «чесний» борг — той, що на нього записали інші. За ним і лише за ним боржник бачить банки кредитора, інакше
    /// номер картки витягнув би будь-хто: записав собі борг перед жертвою, глянув банки, стер запис.
    /// </summary>
    List<(string From, string To, int Amount)> Ledger(string? author)
    {
        var net = new Dictionary<(string, string), long>();
        void Owe(string from, string to, long amount)
        {
            if (from == to || amount == 0) return;
            // Пару ключуємо впорядковано: (a, b) з a < b; додатне — a винен b
            if (string.CompareOrdinal(from, to) < 0) net[(from, to)] = net.GetValueOrDefault((from, to)) + amount;
            else net[(to, from)] = net.GetValueOrDefault((to, from)) - amount;
        }
        foreach (var e in store.Expenses())
        {
            if (author is not null && e.ByPid == author) continue;
            var payer = players.Canon(e.Payer);
            foreach (var (pid, share) in Split(e.Data, e.Payer).Shares) Owe(players.Canon(pid), payer, share);
        }
        foreach (var p in store.Payments())
            if (author is null || p.ByPid != author) Owe(players.Canon(p.From), players.Canon(p.To), -p.Amount);
        return [.. net.Where(kv => kv.Value != 0)
            .Select(kv => kv.Value > 0 ? (kv.Key.Item1, kv.Key.Item2, (int)kv.Value) : (kv.Key.Item2, kv.Key.Item1, (int)-kv.Value))
            .OrderByDescending(x => x.Item3).ThenBy(x => x.Item1, StringComparer.Ordinal)];
    }

    /// <summary>
    /// GET /api/padel/money — особистий баланс. Банки кредитора — лише в owe (я йому винен) і лише коли борг є за
    /// чужими записами (див. <see cref="Ledger(string?)"/>); сума в owe — повна.
    /// </summary>
    public IResult View(PadelMoneyActor me)
    {
        if (!me.User) return PadelMoneyHttp.Fail("Гроші бачать лише акаунти", 403);
        var mine = me.Pid!;
        var ledger = Ledger();
        var honest = Ledger(mine).Where(l => l.From == mine).Select(l => l.To).ToHashSet(StringComparer.Ordinal);
        var owe = ledger.Where(l => l.From == mine).Select(l =>
        {
            var p = players.Player(l.To);
            var banks = Pid.IsUser(l.To) && honest.Contains(l.To) ? BanksView(store.Banks(l.To)) : [];
            return new { pid = l.To, name = p.Name, guest = p.Guest, amount = l.Amount, banks };
        }).ToList();
        var owed = ledger.Where(l => l.To == mine).Select(l =>
        {
            var p = players.Player(l.From);
            return new { pid = l.From, name = p.Name, guest = p.Guest, amount = l.Amount };
        }).ToList();
        return PadelMoneyHttp.Ok(new
        {
            me = mine,
            owe,
            owed,
            all = ledger.Select(l => new { from = l.From, to = l.To, amount = l.Amount }).ToList(),
            expenses = store.Expenses().OrderByDescending(e => e.Id).Take(30).Select(ExpenseView).ToList(),
            payments = store.Payments().OrderByDescending(p => p.Id).Take(50).Select(p => new
            {
                id = p.Key, from = p.From, to = p.To, amount = p.Amount, at = p.At.UtcDateTime, by = players.Name(p.ByPid), byPid = p.ByPid, note = p.Note,
            }).ToList(),
            defaults = new { courtPerHour = options.Value.CourtPerHour, racketPrice = options.Value.RacketPrice },
        });
    }

    /// <summary>Чи є в грошах бодай один запис про цього гравця (як він є, без Canon) — прив'язка гостя з грошима лише адміном.</summary>
    public bool Touches(string pid) =>
        store.Payments().Any(p => p.From == pid || p.To == pid) ||
        store.Expenses().Any(e => e.Payer == pid || e.Data.People.Contains(pid) || e.Data.Rackets.Contains(pid) ||
            e.Data.Other.Any(o => o.Pids?.Contains(pid) == true));

    // ---------------------------------------------------------------- банки

    static List<object> BanksView(List<PadelBank> banks) =>
        [.. banks.Select(b => (object)new { id = b.Id, bank = b.Bank, title = b.Title, card = b.Card, link = b.Link })];

    /// <summary>GET /api/padel/banks — мої; ?pid= — чужі, лише адміну (боржник бачить їх у /money).</summary>
    public IResult Banks(PadelMoneyActor me, string? pid)
    {
        if (!me.User && !me.Admin) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        var whose = string.IsNullOrWhiteSpace(pid) ? me.Pid : players.Canon(pid.Trim());
        if (whose is null) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        if (whose != me.Pid && !me.Admin) return PadelMoneyHttp.Fail("Чужі банки видно лише тим, хто винен", 403);
        return PadelMoneyHttp.Ok(new { banks = BanksView(store.Banks(whose)) });
    }

    public IResult SetBanks(PadelMoneyActor me, BankRequest[]? banks)
    {
        if (!me.User) return PadelMoneyHttp.Fail(PadelMoneyHttp.AccountsOnly, 403);
        var list = banks ?? [];
        if (list.Length > BanksMax) return PadelMoneyHttp.Fail($"Банків — до {BanksMax}");
        var clean = new List<PadelBank>();
        foreach (var b in list)
        {
            if (CleanBank(b, clean.Select(x => x.Id)) is not { } ok) return PadelMoneyHttp.Fail(BankError(b));
            clean.Add(ok);
        }
        store.SetBanks(me.Pid!, clean, clock.UtcNow);
        wire.Money();
        return PadelMoneyHttp.Ok(new { ok = true, banks = BanksView(clean) });
    }

    /// <summary>Що не так із банком (людськими словами); для гарного — порожньо.</summary>
    public static string BankError(BankRequest b)
    {
        var kind = (b.Bank ?? "").Trim().ToLowerInvariant();
        if (!BankKinds.Contains(kind)) return "Невідомий банк";
        if ((b.Title ?? "").Trim().Length > 40) return "Назва — до 40 символів";
        var card = Digits(b.Card);
        var link = (b.Link ?? "").Trim();
        if (card is null && link.Length == 0) return "Додай картку або посилання";
        if (card is not null && (card.Length != 16 || !Luhn(card))) return "Номер картки не той — перевір 16 цифр";
        if (link.Length > 0 && !GoodLink(link)) return "Посилання — https-адреса до 300 символів";
        return "";
    }

    static PadelBank? CleanBank(BankRequest b, IEnumerable<string> taken)
    {
        if (BankError(b).Length > 0) return null;
        var id = (b.Id ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^b[0-9a-z]{1,12}$") || taken.Contains(id))
            id = "b" + Guid.NewGuid().ToString("N")[..8];
        var link = (b.Link ?? "").Trim();
        return new PadelBank(id, b.Bank!.Trim().ToLowerInvariant(), (b.Title ?? "").Trim(), Digits(b.Card), link.Length > 0 ? link : null);
    }

    /// <summary>Цифри картки без пробілів (і дефісів); порожньо — null; щось, крім цифр, — "x" (не пройде перевірки).</summary>
    static string? Digits(string? card)
    {
        var s = (card ?? "").Replace(" ", "").Replace("-", "").Replace(" ", "");
        if (s.Length == 0) return null;
        return s.All(char.IsAsciiDigit) ? s : "x";
    }

    public static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    static bool GoodLink(string link) => link.Length <= 300 && Uri.TryCreate(link, UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps && u.Host.Contains('.');
}

/// <summary>Маршрути грошей: <c>/api/padel/expenses…</c>, <c>/payments…</c>, <c>/money</c>, <c>/banks</c>.</summary>
public static class PadelMoneyApi
{
    public sealed record BanksRequest(PadelMoney.BankRequest[]? Banks);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/expenses/preview", (HttpContext c, PadelMoney.ExpenseRequest b, PadelMoney m) => m.Preview(PadelMoneyActor.Of(c), b));
        api.MapPost("/expenses", (HttpContext c, PadelMoney.ExpenseRequest b, PadelMoney m) => m.Create(PadelMoneyActor.Of(c), b));
        api.MapPut("/expenses/{id}", (string id, HttpContext c, PadelMoney.ExpenseRequest b, PadelMoney m) => m.Update(PadelMoneyActor.Of(c), id, b));
        api.MapDelete("/expenses/{id}", (string id, HttpContext c, PadelMoney m) => m.Delete(PadelMoneyActor.Of(c), id));
        api.MapPost("/payments", (HttpContext c, PadelMoney.PaymentRequest b, PadelMoney m) => m.Pay(PadelMoneyActor.Of(c), b));
        api.MapDelete("/payments/{id}", (string id, HttpContext c, PadelMoney m) => m.Unpay(PadelMoneyActor.Of(c), id));
        api.MapGet("/money", (HttpContext c, PadelMoney m) => m.View(PadelMoneyActor.Of(c)));
        api.MapGet("/banks", (HttpContext c, string? pid, PadelMoney m) => m.Banks(PadelMoneyActor.Of(c), pid));
        api.MapPut("/banks", (HttpContext c, BanksRequest b, PadelMoney m) => m.SetBanks(PadelMoneyActor.Of(c), b.Banks));
    }
}
