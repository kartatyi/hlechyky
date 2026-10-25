using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Чумаки-далекобійники», етап 1 — соло-рейси (контракт — docs/games/specs/fura.md, «Етап 1»; план — D:\or-wt\_fura\PLAN.md).
/// Фізика й керування цілком у браузері (web/games/fura/), сервер веде рейс: дошка з п'яти замовлень, фази
/// free → pickup → haul → done, телеметрія <c>pose</c> до 4/с, суддя (<see cref="FuraJudge"/>) і плата черепками
/// (<c>Award(0, база, "fura:N")</c>, своя денна стеля в <see cref="Economy.Rewards"/>). Стіл особистий і вічний: стан у Save/Load.
/// <para>
/// Реалтайм (TickMs 250) — лише щоб <c>Input pose</c> доходив до гри: каркас пускає Input тільки іграм із тиком. Види
/// після Act у реалтаймі каркас не шле, тож усе, що міняє вид, ставить <see cref="_dirty"/>, і найближчий тик його розсилає.
/// Input стан не зберігає — тому <c>pose</c> приймається й дією (Act): браузер шле її так раз на кілька секунд і в мить
/// зчеплення/відчеплення, і посеред рейсу збережене не відстає від справжнього.
/// </para>
/// </summary>
public sealed class Fura : Game
{
    public override GameInfo Info { get; } = new(
        "fura", "Чумаки-далекобійники", "«Чумаків-далекобійників»", GameGroup.Solo, 1, 1,
        TickMs: 250, Start: StartMode.Immediate, Private: true, Persistent: true,
        Hint: "Тягач із причепом по Чумацькому краю: бери замовлення, чіпляй причіп у відправника й вези. Кермо G29, пад або клавіатура");

    /// <summary>Номер етапу — клієнт показує його, щоб було видно, яка збірка відкрита.</summary>
    public const int Stage = 1;

    /// <summary>Скільки замовлень на дошці.</summary>
    public const int BoardSize = 5;

    public const string Free = "free", Pickup = "pickup", Haul = "haul", Done = "done";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly EconomyOptions Defaults = new();

    FuraMap? _map;
    IOptionsMonitor<EconomyOptions>? _opts;

    string _phase = Free;
    /// <summary>Рейсів зроблено (зданих, хай навіть «усе розбито»). Номер наступного — +1.</summary>
    int _trips;
    string _boardDay = "";
    List<FuraOrder> _board = [];
    FuraOrder? _order;
    DateTimeOffset _takenAt;
    bool _auto;
    FuraJudge _judge = new();
    FuraPose? _last;
    DateTimeOffset _lastAt;
    FuraResult? _result;
    bool _dirty;

    FuraMap Map => _map ??= FuraMap.Get();

    public override string SeatName(int seat) => "чумак";

    /// <summary>Мапу читаємо тут — поза замком кімнати (перший раз це диск).</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options) => _map = FuraMap.Get();

    public override void Start()
    {
        // Сервіси — тут, як у колі: у тестах економіки нема, тоді Boost 1 і стеля типова.
        _opts = Ctx.Services.GetService<IOptionsMonitor<EconomyOptions>>();
        _phase = Free;
        _trips = 0;
        _order = null;
        _auto = false;
        _judge = new();
        _last = null;
        _lastAt = default;
        _result = null;
        _boardDay = "";
        _board = [];
        EnsureBoard();
        _dirty = true;
    }

    // ---------- економіка ----------

    EconomyOptions Econ => _opts?.CurrentValue ?? Defaults;
    int Earn(int n) => Econ.Earn(n);

    // ---------- дошка ----------

    string NickKey => Rooms.NickKey(Ctx.NickOf(0) ?? "");

    /// <summary>Нова дошка, якщо її ще нема чи настав інший день (лише у фазі free). true — дошку змінено.</summary>
    bool EnsureBoard()
    {
        if (_phase != Free) return false;
        var day = Days.Today(Ctx.Clock);
        if (_boardDay == day && _board.Count > 0) return false;
        _board = MakeBoard(Map, day, NickKey, _trips);
        _boardDay = day;
        return true;
    }

    /// <summary>
    /// П'ять замовлень детерміновано від дня, ніка й кількості рейсів (сід — <see cref="Days.Seed"/>, не <c>Ctx.Rng</c>):
    /// та сама людина того самого дня після того самого рейсу бачить ту саму дошку, хоч скільки перевідкривай.
    /// </summary>
    public static List<FuraOrder> MakeBoard(FuraMap map, string day, string nickKey, int trips)
    {
        var rng = new Random(Days.Seed("fura", $"{day}:{nickKey}:{trips}"));
        var combos = new List<(FuraCargo Cargo, FuraPlace From, FuraPlace To)>();
        foreach (var c in map.Cargo)
            foreach (var f in c.From)
                foreach (var t in c.To)
                    if (f != t && map.Place(f) is { } from && map.Place(t) is { } to && double.IsFinite(map.Meters(f, t)))
                        combos.Add((c, from, to));
        for (var i = combos.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (combos[i], combos[j]) = (combos[j], combos[i]);
        }
        var list = new List<FuraOrder>(BoardSize);
        foreach (var (cargo, from, to) in combos.Take(BoardSize))
        {
            var dock = from.Docks[rng.Next(from.Docks.Length)];
            var km = map.Km(from.Id, to.Id);
            list.Add(new FuraOrder(list.Count + 1, cargo.Id, from.Id, dock.Id, to.Id, km, FuraJudge.Minutes(km),
                FuraJudge.Pay(km, cargo.Mult, 100, false, false, false)));
        }
        return list;
    }

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        var now = Ctx.Clock.UtcNow;
        switch (action)
        {
            case "pose":
                Pose(ReadPose(payload), now);
                return ActResult.Done;

            case "take":
            {
                if (_phase != Free) return ActResult.Fail(_phase == Done ? "Спершу глянь підсумок і натисни «Далі»" : "Ти вже везеш замовлення");
                var id = ReadId(payload);
                if (_board.FirstOrDefault(o => o.Id == id) is not { } order) return ActResult.Fail("Нема такого замовлення на дошці");
                _order = order;
                _phase = Pickup;
                _takenAt = now;
                _auto = false;
                _judge = FuraJudge.Begin(now, _last, _lastAt);
                _dirty = true;
                return ActResult.Accept($"Причіп чекає: {Map.Place(order.From)!.Short}, бокс {order.Dock}");
            }

            case "cancel":
                if (_phase is not (Pickup or Haul)) return ActResult.Fail("Нема що скасовувати");
                Drop();
                return ActResult.Accept("Замовлення скасовано");

            case "auto":
            {
                if (_phase != Pickup || _order is null) return ActResult.Fail("Автозчеп — лише поки причіп чекає у відправника");
                var dock = Map.Place(_order.From)!.Dock(_order.Dock)!;
                if (_last is null || !FuraJudge.NearForAuto(_last, dock))
                    return ActResult.Fail($"Під'їдь до боксу {_order.Dock} ближче ніж на {FuraJudge.AutoM:0} м");
                if (_auto) return ActResult.Done;
                _auto = true;
                _dirty = true;
                return ActResult.Accept("Автозчеп: −10 % до плати");
            }

            case "rescue":
                if (!_judge.Rescue) { _judge.Rescue = true; _dirty = true; }
                return ActResult.Done;

            case "deliver":
                return Deliver(now);

            case "next":
                if (_phase != Done) return ActResult.Fail("Рейс ще не здано");
                _phase = Free;
                _result = null;
                _order = null;
                _board = [];   // рейсів побільшало — інший сід, дошка нова навіть того самого дня
                EnsureBoard();
                _dirty = true;
                return ActResult.Done;

            default:
                return ActResult.Fail("Тут так не їздять");
        }
    }

    /// <summary>Скасувати рейс → free з тією самою дошкою (або новою, якщо вже інший день).</summary>
    void Drop()
    {
        _phase = Free;
        _order = null;
        _auto = false;
        _judge = new();
        EnsureBoard();
        _dirty = true;
    }

    void Pose(FuraPose p, DateTimeOffset now)
    {
        if (_phase is Pickup or Haul && _judge.Feed(p, now)) _dirty = true;
        _last = p;
        _lastAt = now;
        if (_phase == Pickup && _order is not null && FuraJudge.PickedUp(p, Map.Place(_order.From)!.Dock(_order.Dock)!))
        {
            _phase = Haul;
            _judge.StartHaul(p);
            _dirty = true;
        }
    }

    /// <summary>
    /// Здача. Спершу — чи рейс не зіпсовано по дорозі (тоді скасовуємо). Далі «тут і зараз» (зупинись, відчепи, заїдь у двір) —
    /// відмова без зміни стану. І лише на місці — час і кілометри: так випадковий «Здати» посеред дороги рейсу не губить.
    /// </summary>
    ActResult Deliver(DateTimeOffset now)
    {
        if (_phase != Haul || _order is null) return ActResult.Fail(_phase == Pickup ? "Спершу зачепи причіп у відправника" : "Нема що здавати");
        var order = _order;
        var to = Map.Place(order.To)!;
        if (_judge.Void is { } spoiled) { Drop(); return ActResult.Fail(spoiled); }
        if (FuraJudge.NotYet(_last, _lastAt, now, to) is { } wait) return ActResult.Fail(wait);
        if (_judge.Verdict(order.Km) is { } bad) { Drop(); return ActResult.Fail(bad); }

        var last = _last!;
        var onTime = _judge.TripSec <= order.Minutes * 60;
        var exact = FuraJudge.Exact(last, to);
        var k = _judge.MinK;
        var mult = Map.CargoOf(order.Cargo)?.Mult ?? 1;
        var pay = FuraJudge.Pay(order.Km, mult, k, onTime, exact, _auto);
        _trips++;
        _result = new FuraResult(_trips, order, Math.Round(_judge.HaulM / 1000, 2), (int)Math.Round(_judge.TripSec),
            k, onTime, exact, _auto, pay);
        if (pay > 0) Ctx.Award(0, pay, $"fura:{_trips}");
        _phase = Done;
        _auto = false;
        _judge = new();
        _dirty = true;
        return ActResult.Accept(pay > 0 ? $"Рейс №{_trips} здано" : "Рейс здано, але все розбито — плати нема");
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        if (EnsureBoard()) _dirty = true;   // північ за Києвом застала на дошці — нова дошка
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    /// <summary>Кадрів нема: усе, що треба браузеру, — у виді, а поза — його ж власна.</summary>
    public override object? Frame() => null;

    // ---------- розбір payload ----------

    static int ReadId(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var n)) return n;
            if (id.ValueKind == JsonValueKind.String && int.TryParse(id.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        }
        throw new GameError("Яке замовлення?");
    }

    /// <summary><c>{x, z, v, c, k, rx, rz, ry}</c>: x, z, rx, rz обов'язкові; без v — 0, c — false, k — 100, ry — 0.</summary>
    public static FuraPose ReadPose(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) throw new GameError("Нема пози");
        double Num(string name, double? fallback)
        {
            if (p.TryGetProperty(name, out var e))
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) && double.IsFinite(v)) return v;
            }
            else if (fallback is { } f) return f;
            throw new GameError("Погана поза");
        }
        var c = p.TryGetProperty("c", out var ce) && (ce.ValueKind == JsonValueKind.True
            || (ce.ValueKind == JsonValueKind.Number && ce.TryGetDouble(out var cn) && cn != 0));
        var k = (int)Math.Round(Math.Clamp(Num("k", 100), 0, 100));
        return new FuraPose(Num("x", null), Num("z", null), Num("v", 0), c, k, Num("rx", null), Num("rz", null), Num("ry", 0));
    }

    // ---------- вид ----------

    public override object View(int? seat) => new
    {
        stage = Stage,
        map = Map.Id,
        phase = _phase,
        trips = _trips,
        board = _phase == Free ? _board.Select(OrderView).ToArray() : null,
        order = _order is null ? null : OrderView(_order),
        trip = _phase is Pickup or Haul ? new
        {
            takenAt = _takenAt,
            sec = (int)Math.Round(_judge.TripSec),
            haulSec = (int)Math.Round(_judge.HaulSec),
            driven = Math.Round(_judge.HaulM / 1000, 2),
            minK = _judge.MinK,
            auto = _auto,
            rescue = _judge.Rescue,
            violations = _judge.Violations,
            voided = _judge.Void,
        } : null,
        pose = _last is null ? null : new
        {
            x = _last.X, z = _last.Z, v = _last.V, c = _last.C, k = _last.K, rx = _last.Rx, rz = _last.Rz, ry = _last.Ry,
            at = _lastAt,
        },
        result = _result is null ? null : new
        {
            trip = _result.Trip,
            cargo = _result.Order.Cargo,
            from = _result.Order.From,
            to = _result.Order.To,
            km = _result.Order.Km,
            driven = _result.Driven,
            sec = _result.Sec,
            minutes = _result.Order.Minutes,
            k = _result.K,
            onTime = _result.OnTime,
            exact = _result.Exact,
            auto = _result.Auto,
            broken = _result.K < FuraJudge.BrokenK,
            @base = _result.Base,
            shards = Earn(_result.Base),
        },
        cap = Earn(Econ.FuraDailyCap),
    };

    object OrderView(FuraOrder o) => new
    {
        id = o.Id, cargo = o.Cargo, from = o.From, dock = o.Dock, to = o.To, km = o.Km, minutes = o.Minutes, pay = Earn(o.Base),
    };

    // ---------- Save/Load ----------

    sealed record Saved(int V, string Phase, int Trips, string BoardDay, FuraOrder[] Board, FuraOrder? Order,
        DateTimeOffset TakenAt, bool Auto, FuraJudge Judge, FuraPose? Last, DateTimeOffset LastAt, FuraResult? Result);

    public override string? Save() => JsonSerializer.Serialize(
        new Saved(1, _phase, _trips, _boardDay, [.. _board], _order, _takenAt, _auto, _judge, _last, _lastAt, _result), Json);

    public override void Load(string json)
    {
        Saved? s;
        try { s = JsonSerializer.Deserialize<Saved>(json, Json); }
        catch (JsonException) { return; }
        if (s is null || s.Phase is not (Free or Pickup or Haul or Done) || s.Trips < 0) return;
        bool Valid(FuraOrder? o) => o is { Cargo: not null, From: not null, To: not null, Dock: not null }
            && Map.CargoOf(o.Cargo) is not null && Map.Place(o.To) is not null && Map.Place(o.From)?.Dock(o.Dock) is not null;
        var board = (s.Board ?? []).ToList();
        if (!board.All(Valid)) board = [];
        if (s.Phase is Pickup or Haul && !Valid(s.Order)) return;
        if (s.Phase == Done && (s.Result is null || !Valid(s.Result.Order))) return;

        _phase = s.Phase;
        _trips = s.Trips;
        _boardDay = s.BoardDay ?? "";
        _board = board;
        _order = s.Phase == Free ? null : s.Order;
        _takenAt = s.TakenAt;
        _auto = s.Auto;
        _judge = s.Judge ?? new();
        _last = s.Last;
        _lastAt = s.LastAt;
        _result = s.Phase == Done ? s.Result : null;
        EnsureBoard();
        _dirty = true;
    }
}

/// <summary>Замовлення на дошці. <see cref="Base"/> — орієнтовна база плати (ціле 100 %, без бонусів), у виді — × Boost.</summary>
public sealed record FuraOrder(int Id, string Cargo, string From, string Dock, string To, double Km, int Minutes, int Base);

/// <summary>Підсумок зданого рейсу: проїхано (км), час без пауз (с), ціле, бонуси й база плати (до × Boost).</summary>
public sealed record FuraResult(int Trip, FuraOrder Order, double Driven, int Sec, int K, bool OnTime, bool Exact, bool Auto, int Base);
