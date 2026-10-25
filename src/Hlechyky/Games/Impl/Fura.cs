using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Чумаки-далекобійники» (контракт — docs/games/specs/fura.md, «Етап 1» і «Етап 2 — колона»; план — D:\or-wt\_fura\PLAN.md).
/// Фізика й керування цілком у браузері (web/games/fura/), сервер веде рейси: у кожного місця своя «фура»
/// (<see cref="FuraRig"/>: дошка з п'яти замовлень, фази free → pickup → haul → done, суддя <see cref="FuraJudge"/>),
/// плата черепками (<c>Award(seat, база, "fura:N")</c>, своя денна стеля в <see cref="Economy.Rewards"/>) і колонне
/// замовлення <c>id: "k"</c> на всіх за столом (<see cref="FuraConvoy"/>, +15 % тим, хто здав у вікні 5 хв).
/// <para>
/// Стіл до восьми фур: соло — стіл на одного, друг сідає посеред рейсу (LateJoin), рейс не рветься. Фура належить ніку:
/// вантажиться з <see cref="FuraGarage"/>, коли нік сідає, пишеться туди після кожного Act і в OnLeave (не в тику —
/// запис у базу в тику смикає реалтайм). Реалтайм (TickMs 100): <c>Input pose</c> до 10/с, кадр привидів (<see cref="Frame"/>),
/// коли фур ≥ 2. Види після Act каркас у реалтаймі не шле — дія ставить <see cref="_dirty"/>, найближчий тик розсилає.
/// </para>
/// </summary>
public sealed class Fura : Game
{
    public override GameInfo Info { get; } = new(
        "fura", "Чумаки-далекобійники", "«Чумаків-далекобійників»", GameGroup.Solo, 1, 8,
        TickMs: 100, Start: StartMode.Immediate,
        Hint: "Тягач із причепом по Чумацькому краю: бери замовлення, чіпляй причіп у відправника й вези — сам чи колоною до восьми фур. Кермо G29, пад або клавіатура");

    /// <summary>Номер етапу — клієнт показує його, щоб було видно, яка збірка відкрита.</summary>
    public const int Stage = 2;

    /// <summary>Скільки замовлень на дошці (без колонного).</summary>
    public const int BoardSize = 5;

    /// <summary>Id колонного замовлення на дошці й у <c>take</c>.</summary>
    public const string ColId = "k";

    /// <summary>Привид — лише з позою, свіжішою за це.</summary>
    public const double GhostFreshSec = 3;

    /// <summary>Поза (Act чи Input — для гри однаково) пишеться в гараж не частіше за це, хіба змінились фаза чи зчеплення.</summary>
    public const double KeepPoseSec = 5;

    public const string Free = "free", Pickup = "pickup", Haul = "haul", Done = "done";

    /// <summary>Порожній стіл чекає свого (оновив сторінку — повернувся за той самий стіл).</summary>
    public override TimeSpan HoldEmpty => TimeSpan.FromSeconds(60);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly EconomyOptions Defaults = new();

    FuraMap? _map;
    IOptionsMonitor<EconomyOptions>? _opts;
    FuraGarage _garage = null!;

    FuraRig?[] _rigs = [];
    /// <summary>Номер колони, що йде, або наступної.</summary>
    int _colNo = 1;
    FuraConvoy? _col;
    /// <summary>Колонне на дошці, поки ніхто не взяв: (день, номер) → замовлення.</summary>
    (string Day, int No, FuraOrder? Order) _colOffer;
    long _tick;
    bool _dirty;

    FuraMap Map => _map ??= FuraMap.Get();

    public override string SeatName(int seat) => "чумак";

    /// <summary>Мапу читаємо тут — поза замком кімнати (перший раз це диск).</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options) => _map = FuraMap.Get();

    public override void Start()
    {
        // Сервіси — тут, як у колі: у тестах економіки нема, тоді Boost 1 і стеля типова; гаража нема — свій у пам'яті.
        _opts = Ctx.Services.GetService<IOptionsMonitor<EconomyOptions>>();
        _garage = Ctx.Services.GetService<FuraGarage>() ?? new FuraGarage(new MemoryGameStore(), a => a());
        _rigs = new FuraRig?[Info.MaxPlayers];
        _colNo = 1;
        _col = null;
        _colOffer = default;
        _tick = 0;
        for (var s = 0; s < _rigs.Length; s++)
            if (Ctx.Seated(s)) Sit(s);
        _dirty = true;
    }

    // ---------- економіка ----------

    EconomyOptions Econ => _opts?.CurrentValue ?? Defaults;
    int Earn(int n) => Econ.Earn(n);

    // ---------- фури за столом ----------

    public override bool LateJoin(string nick) => true;
    public override string? LateJoinGreeting(string nick) => "Сідай до колони! Твоя фура — там, де ти її лишив";

    public override void OnJoin(int seat)
    {
        Sit(seat);
        if (_rigs[seat] is { } rig && _col?.Member(rig.NickKey) is { } m) m.AwaySince = null;
        _dirty = true;
    }

    /// <summary>Не техпоразка: фура в гараж, місце вільне, решта їде далі. Колона чекає на нього хвилину.</summary>
    public override void OnLeave(int seat)
    {
        if (_rigs[seat] is { } rig)
        {
            Keep(rig);
            _garage.Release(rig.NickKey, rig.Claim);
            if (_col?.Member(rig.NickKey) is { DoneAt: null } m) m.AwaySince = Ctx.Clock.UtcNow;
        }
        _rigs[seat] = null;
        _dirty = true;
    }

    /// <summary>
    /// Нік сів: фура з гаража (свіжа з пам'яті чи з бази). Запису нема зовсім — зі старого соло-запису етапу 1 (рейси й
    /// незакінчений рейс; старий не видаляємо), а нема й того — нова. Запис є, але нечитний — нова фура, проте номер рейсу
    /// не йде назад: найбільший, що вдасться дістати з нього чи зі старого запису. Стіл забирає заявку: інший стіл цей нік
    /// більше не судить.
    /// </summary>
    void Sit(int seat)
    {
        var nick = Ctx.NickOf(seat) ?? "";
        var key = Rooms.NickKey(nick);
        var raw = _garage.Load(key);
        var rig = FuraRig.Parse(raw, Map);
        var fix = false;
        if (raw is null)
        {
            rig = FuraRig.Parse(_garage.LoadOld(key), Map);
            fix = rig is not null;
        }
        else if (rig is null)
        {
            var old = _garage.LoadOld(key);
            rig = new FuraRig { Trips = Math.Max(FuraRig.TripsOf(raw), FuraRig.Parse(old, Map)?.Trips ?? FuraRig.TripsOf(old)) };
            fix = true;
        }
        rig ??= new FuraRig();
        rig.Nick = nick;
        rig.NickKey = key;
        rig.Claim = _garage.Claim(key);
        _rigs[seat] = rig;
        EnsureBoard(rig);
        if (fix) Keep(rig);
    }

    /// <summary>Фуру — в гараж (запис у базу — з пулу потоків). Забрав інший стіл — тихо нічого.</summary>
    void Keep(FuraRig rig, DateTimeOffset? now = null)
    {
        rig.KeptAt = now ?? Ctx.Clock.UtcNow;
        _garage.Save(rig.NickKey, rig.Claim, rig.ToJson());
    }

    bool Owned(FuraRig rig) => _garage.Owns(rig.NickKey, rig.Claim);

    /// <summary>Скільки фур за столом (сидять і судяться тут).</summary>
    int RigCount
    {
        get
        {
            var n = 0;
            foreach (var r in _rigs) if (r is not null) n++;
            return n;
        }
    }

    int SeatOfKey(string nickKey)
    {
        for (var s = 0; s < _rigs.Length; s++)
            if (_rigs[s]?.NickKey == nickKey) return s;
        return -1;
    }

    // ---------- дошка ----------

    /// <summary>Нова дошка, якщо її ще нема чи настав інший день (лише у фазі free). true — дошку змінено.</summary>
    bool EnsureBoard(FuraRig rig)
    {
        if (rig.Phase != Free) return false;
        var day = Days.Today(Ctx.Clock);
        if (rig.BoardDay == day && rig.Board.Count > 0) return false;
        rig.Board = MakeBoard(Map, day, rig.NickKey, rig.Trips);
        rig.BoardDay = day;
        return true;
    }

    /// <summary>
    /// П'ять замовлень детерміновано від дня, ніка й кількості рейсів (сід — <see cref="Days.Seed"/>, не <c>Ctx.Rng</c>):
    /// та сама людина того самого дня після того самого рейсу бачить ту саму дошку, хоч скільки перевідкривай.
    /// </summary>
    public static List<FuraOrder> MakeBoard(FuraMap map, string day, string nickKey, int trips)
    {
        var rng = new Random(Days.Seed("fura", $"{day}:{nickKey}:{trips}"));
        var combos = Combos(map, rng, _ => true);
        var list = new List<FuraOrder>(BoardSize);
        foreach (var (cargo, from, to) in combos.Take(BoardSize))
            list.Add(MakeOrder(map, list.Count + 1, cargo, from, to, from.Docks[rng.Next(from.Docks.Length)]));
        return list;
    }

    /// <summary>
    /// Колонне замовлення стола: сід — <c>hash(день, кімната, номер колони)</c>; відправник — з двома боксами й більше
    /// (колона на одного не колона). <see cref="FuraOrder.Dock"/> — бокс, з якого почнеться коло.
    /// </summary>
    public static FuraOrder? MakeColOrder(FuraMap map, string day, string roomId, int no)
    {
        var rng = new Random(Days.Seed("fura", $"{day}:{roomId}:k{no}"));
        foreach (var (cargo, from, to) in Combos(map, rng, p => p.Docks.Length >= 2).Take(1))
            return MakeOrder(map, 0, cargo, from, to, from.Docks[rng.Next(from.Docks.Length)]) with { Col = true };
        return null;
    }

    static List<(FuraCargo Cargo, FuraPlace From, FuraPlace To)> Combos(FuraMap map, Random rng, Func<FuraPlace, bool> fromOk)
    {
        var combos = new List<(FuraCargo Cargo, FuraPlace From, FuraPlace To)>();
        foreach (var c in map.Cargo)
            foreach (var f in c.From)
                foreach (var t in c.To)
                    if (f != t && map.Place(f) is { } from && fromOk(from) && map.Place(t) is { } to && double.IsFinite(map.Meters(f, t)))
                        combos.Add((c, from, to));
        for (var i = combos.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (combos[i], combos[j]) = (combos[j], combos[i]);
        }
        return combos;
    }

    static FuraOrder MakeOrder(FuraMap map, int id, FuraCargo cargo, FuraPlace from, FuraPlace to, FuraDock dock)
    {
        var km = map.Km(from.Id, to.Id);
        return new FuraOrder(id, cargo.Id, from.Id, dock.Id, to.Id, km, FuraJudge.Minutes(km),
            FuraJudge.Pay(km, cargo.Mult, 100, false, false, false));
    }

    /// <summary>Колонне, яке зараз видно на дошці: те, що йде, або наступне (кешоване на день і номер).</summary>
    FuraOrder? ColOrder()
    {
        if (_col is not null) return _col.Order;
        var day = Days.Today(Ctx.Clock);
        if (_colOffer.Day != day || _colOffer.No != _colNo)
            _colOffer = (day, _colNo, MakeColOrder(Map, day, Ctx.RoomId, _colNo));
        return _colOffer.Order;
    }

    /// <summary>Чи показати фурі колонне шостим: за столом ≥ 2 фур і колона або ще не йде, або ще ніхто не здав і її тут нема.</summary>
    bool OffersCol(FuraRig rig) =>
        rig.Phase == Free && RigCount >= 2
        && (_col is null || (_col.FirstAt is null && _col.Member(rig.NickKey) is null));

    // ---------- дії ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (seat < 0 || seat >= _rigs.Length || _rigs[seat] is not { } rig) return ActResult.Fail("Ти не за кермом");
        if (!Owned(rig)) return ActResult.Fail("Ти вже в рейсі за іншим столом");
        var now = Ctx.Clock.UtcNow;
        var (phase, hitched) = (rig.Phase, rig.Last?.C);
        var result = Do(seat, rig, action, payload, now);
        // Писати після кожної дії цього місця (take, deliver…), не в тику. Поза — виняток: каркас кличе Act і для Input
        // (10/с), і гра їх не розрізняє, тож поза пишеться раз на KeepPoseSec або коли змінились фаза чи зчеплення —
        // так само часто, як браузер шле act pose.
        if (action != "pose" || rig.Phase != phase || rig.Last?.C != hitched || (now - rig.KeptAt).TotalSeconds >= KeepPoseSec)
            Keep(rig, now);
        return result;
    }

    ActResult Do(int seat, FuraRig rig, string action, JsonElement payload, DateTimeOffset now)
    {
        switch (action)
        {
            case "pose":
                Pose(rig, ReadPose(payload), now);
                return ActResult.Done;

            case "take":
            {
                if (rig.Phase != Free) return ActResult.Fail(rig.Phase == Done ? "Спершу глянь підсумок і натисни «Далі»" : "Ти вже везеш замовлення");
                if (IsCol(payload)) return TakeCol(rig, now);
                var id = ReadId(payload);
                if (rig.Board.FirstOrDefault(o => o.Id == id) is not { } order) return ActResult.Fail("Нема такого замовлення на дошці");
                Begin(rig, order, now);
                return ActResult.Accept($"Причіп чекає: {Map.Place(order.From)!.Short}, бокс {order.Dock}");
            }

            case "cancel":
                if (rig.Phase is not (Pickup or Haul)) return ActResult.Fail("Нема що скасовувати");
                var inCol = LeaveCol(rig);
                Drop(rig);
                return ActResult.Accept(inCol ? "Ти вийшов з колони" : "Замовлення скасовано");

            case "auto":
            {
                if (rig.Phase != Pickup || rig.Order is null) return ActResult.Fail("Автозчеп — лише поки причіп чекає у відправника");
                var dock = Map.Place(rig.Order.From)!.Dock(rig.Order.Dock)!;
                if (rig.Last is null || !FuraJudge.NearForAuto(rig.Last, dock))
                    return ActResult.Fail($"Під'їдь до боксу {rig.Order.Dock} ближче ніж на {FuraJudge.AutoM:0} м");
                if (rig.Auto) return ActResult.Done;
                rig.Auto = true;
                _dirty = true;
                return ActResult.Accept("Автозчеп: −10 % до плати");
            }

            case "rescue":
                if (!rig.Judge.Rescue) { rig.Judge.Rescue = true; _dirty = true; }
                return ActResult.Done;

            case "deliver":
                return Deliver(seat, rig, now);

            case "next":
                if (rig.Phase != Done) return ActResult.Fail("Рейс ще не здано");
                rig.Phase = Free;
                rig.Result = null;
                rig.Order = null;
                rig.Board = [];   // рейсів побільшало — інший сід, дошка нова навіть того самого дня
                EnsureBoard(rig);
                _dirty = true;
                return ActResult.Done;

            default:
                return ActResult.Fail("Тут так не їздять");
        }
    }

    void Begin(FuraRig rig, FuraOrder order, DateTimeOffset now)
    {
        rig.Order = order;
        rig.Phase = Pickup;
        rig.TakenAt = now;
        rig.Auto = false;
        rig.Judge = FuraJudge.Begin(now, rig.Last, rig.LastAt);
        _dirty = true;
    }

    /// <summary>
    /// <c>take {id: "k"}</c>: нема колони — стартує нову (треба хоч дві фури за столом); є й ніхто ще не здав — приєднатися.
    /// Бокс — наступний вільний по колу боксів відправника; усі зайняті — «Колона повна».
    /// </summary>
    ActResult TakeCol(FuraRig rig, DateTimeOffset now)
    {
        var starting = _col is null;
        if (starting)
        {
            if (RigCount < 2) return ActResult.Fail("Колонне — коли за столом хоч дві фури");
            if (ColOrder() is not { } order) return ActResult.Fail("Колонного сьогодні нема");
            _col = new FuraConvoy { No = _colNo, Order = order };
        }
        var col = _col!;
        if (col.FirstAt is not null) return ActResult.Fail("Колона вже здає — бери своє замовлення");
        if (col.Member(rig.NickKey) is not null) return ActResult.Fail("Ти вже в колоні");
        var from = Map.Place(col.Order.From)!;
        if (col.NextDock(from) is not { } dock)
        {
            if (starting) _col = null;
            return ActResult.Fail("Колона повна");
        }
        col.Members.Add(new FuraMember { NickKey = rig.NickKey, Nick = rig.Nick, Dock = dock });
        col.Joins++;
        rig.ColRoom = Ctx.RoomId;
        rig.ColNo = col.No;
        Begin(rig, col.Order with { Dock = dock }, now);
        return ActResult.Accept(starting
            ? $"Колона рушає! Причіп чекає: {from.Short}, бокс {dock}"
            : $"Ти в колоні. Причіп чекає: {from.Short}, бокс {dock}");
    }

    /// <summary>Фура в колоні, що йде за цим столом, — її учасник (null — везе як своє).</summary>
    FuraMember? ColMember(FuraRig rig) =>
        rig.Order is { Col: true } && _col is { } col && rig.ColRoom == Ctx.RoomId && rig.ColNo == col.No ? col.Member(rig.NickKey) : null;

    /// <summary>Вийти з колони (cancel). true — був у колоні. Колона без тих, хто ще везе, закінчується.</summary>
    bool LeaveCol(FuraRig rig)
    {
        if (ColMember(rig) is not { } m) return false;
        m.Left = true;
        if (_col!.Active == 0) EndCol();
        return true;
    }

    void EndCol()
    {
        _col = null;
        _colNo++;
        _dirty = true;
    }

    /// <summary>Скасувати рейс → free з тією самою дошкою (або новою, якщо вже інший день).</summary>
    void Drop(FuraRig rig)
    {
        rig.Phase = Free;
        rig.Order = null;
        rig.Auto = false;
        rig.Judge = new();
        EnsureBoard(rig);
        _dirty = true;
    }

    void Pose(FuraRig rig, FuraPose p, DateTimeOffset now)
    {
        if (rig.Phase is Pickup or Haul && rig.Judge.Feed(p, now)) _dirty = true;
        rig.Last = p;
        rig.LastAt = now;
        if (rig.Phase == Pickup && rig.Order is not null && FuraJudge.PickedUp(p, Map.Place(rig.Order.From)!.Dock(rig.Order.Dock)!))
        {
            rig.Phase = Haul;
            rig.Judge.StartHaul(p);
            _dirty = true;
        }
    }

    /// <summary>
    /// Здача. Спершу — чи рейс не зіпсовано по дорозі (тоді скасовуємо). Далі «тут і зараз» (зупинись, відчепи, заїдь у двір) —
    /// відмова без зміни стану. І лише на місці — час і кілометри: так випадковий «Здати» посеред дороги рейсу не губить.
    /// Колонне — той самий суддя й та сама формула, плюс бонус колони (<see cref="ColDone"/>).
    /// </summary>
    ActResult Deliver(int seat, FuraRig rig, DateTimeOffset now)
    {
        if (rig.Phase != Haul || rig.Order is null) return ActResult.Fail(rig.Phase == Pickup ? "Спершу зачепи причіп у відправника" : "Нема що здавати");
        var order = rig.Order;
        var to = Map.Place(order.To)!;
        if (rig.Judge.Void is { } spoiled) { LeaveCol(rig); Drop(rig); return ActResult.Fail(spoiled); }
        if (FuraJudge.NotYet(rig.Last, rig.LastAt, now, to) is { } wait) return ActResult.Fail(wait);
        if (rig.Judge.Verdict(order.Km) is { } bad) { LeaveCol(rig); Drop(rig); return ActResult.Fail(bad); }

        var last = rig.Last!;
        // Термін — на сам рейс із вантажем (від зчеплення у відправника): км у замовленні — це шлях від відправника до
        // отримувача, а дорога порожняком до причепа в нього не входить (25.10: перший же рейс «запізнився» на 22 с саме так).
        var onTime = rig.Judge.HaulSec <= order.Minutes * 60;
        var exact = FuraJudge.Exact(last, to);
        var k = rig.Judge.MinK;
        var mult = Map.CargoOf(order.Cargo)?.Mult ?? 1;
        var pay = FuraJudge.Pay(order.Km, mult, k, onTime, exact, rig.Auto);
        rig.Trips++;
        rig.Result = new FuraResult(rig.Trips, order, Math.Round(rig.Judge.HaulM / 1000, 2), (int)Math.Round(rig.Judge.TripSec),
            k, onTime, exact, rig.Auto, pay, order.Col);
        if (pay > 0) Ctx.Award(seat, pay, $"fura:{rig.Trips}");
        rig.Phase = Done;
        rig.Auto = false;
        rig.Judge = new();
        _dirty = true;
        var bonus = ColDone(rig, pay, now);
        return ActResult.Accept(pay <= 0 ? "Рейс здано, але все розбито — плати нема"
            : bonus ? $"Рейс №{rig.Trips} здано — колоні +15 %" : $"Рейс №{rig.Trips} здано");
    }

    /// <summary>
    /// Колонне здано у вікні: запам'ятати, а коли здав хоч другий — кожному, хто здав у вікні й досі за столом, окрема
    /// нагорода ⌈база × 0,15⌉ з reason <c>fura:&lt;N&gt;k</c>, раз. Хто встиг піти — без бонусу. true — бонус щойно був.
    /// </summary>
    bool ColDone(FuraRig rig, int pay, DateTimeOffset now)
    {
        if (ColMember(rig) is not { } m || _col is not { } col) return false;
        if (col.FirstAt is { } first && now - first > FuraConvoy.Window) return false;
        m.DoneAt = now;
        m.Trip = rig.Trips;
        m.Base = pay;
        col.FirstAt ??= now;
        var paid = false;
        // «Здали разом» — лише ті, кому є за що платити: розбите вщент (база 0) поріг не рахує.
        if (col.Members.Count(x => !x.Left && x.DoneAt is not null && x.Base > 0) >= 2)
            foreach (var x in col.Members)
            {
                if (x.Left || x.DoneAt is null || x.Bonused) continue;
                var s = SeatOfKey(x.NickKey);
                if (s < 0 || _rigs[s] is not { } who || !Owned(who)) continue;   // пішов з-за столу — без бонусу
                x.Bonused = true;
                var bonus = (int)Math.Ceiling(Math.Round(x.Base * FuraConvoy.BonusShare, 6));
                if (bonus <= 0) continue;
                Ctx.Award(s, bonus, $"fura:{x.Trip}k");
                paid = true;
                if (who.Result is { } r && r.Trip == x.Trip)
                {
                    who.Result = r with { Bonus = bonus };
                    if (who != rig) Keep(who);
                }
            }
        if (col.Active == 0) EndCol();
        return paid;
    }

    // ---------- тик ----------

    public override TickResult Tick()
    {
        _tick++;
        var now = Ctx.Clock.UtcNow;
        foreach (var rig in _rigs)
            if (rig is not null && EnsureBoard(rig)) _dirty = true;   // північ за Києвом застала на дошці — нова дошка
        if (_col is { } col)
        {
            // Хто встав, не здавши, і не повернувся за хвилину — з колони (довезе як своє).
            foreach (var m in col.Members)
                if (!m.Left && m.DoneAt is null && m.AwaySince is { } away && now - away >= HoldEmpty)
                {
                    m.Left = true;
                    _dirty = true;
                }
            if ((col.FirstAt is { } first && now - first > FuraConvoy.Window) || (col.Members.Count > 0 && col.Active == 0))
                EndCol();
        }
        var frame = RigCount >= 2;
        if (!_dirty) return frame ? TickResult.FrameOnly : TickResult.None;
        _dirty = false;
        return new TickResult(frame, true);
    }

    /// <summary>
    /// Кадр привидів (один на всіх, пласкі цілі): <c>{ t, p: [seat, x·10, z·10, h·1000, v·10, c, rx·10, rz·10, ry·1000, f, …] }</c>
    /// — 10 чисел на кожну фуру з позою, свіжішою за 3 с. Соло (фура одна) — null: кадру нема.
    /// </summary>
    public override object? Frame()
    {
        if (RigCount < 2) return null;
        var now = Ctx.Clock.UtcNow;
        var p = new List<int>(_rigs.Length * 10);
        for (var s = 0; s < _rigs.Length; s++)
        {
            if (_rigs[s] is not { Last: { } q } rig || (now - rig.LastAt).TotalSeconds > GhostFreshSec || !Owned(rig)) continue;
            p.Add(s);
            p.Add(I(q.X * 10)); p.Add(I(q.Z * 10)); p.Add(I(q.H * 1000)); p.Add(I(q.V * 10)); p.Add(q.C ? 1 : 0);
            p.Add(I(q.Rx * 10)); p.Add(I(q.Rz * 10)); p.Add(I(q.Ry * 1000)); p.Add(q.F);
        }
        return new { t = _tick, p = p.ToArray() };
    }

    static int I(double v) => (int)Math.Round(Math.Clamp(v, int.MinValue, int.MaxValue));

    // ---------- розбір payload ----------

    static bool IsCol(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String && id.GetString() == ColId;

    static int ReadId(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var n)) return n;
            if (id.ValueKind == JsonValueKind.String && int.TryParse(id.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        }
        throw new GameError("Яке замовлення?");
    }

    /// <summary>
    /// <c>{x, z, v, c, k, rx, rz, ry, h, f}</c>: x, z, rx, rz обов'язкові; без v — 0, c — false, k — 100, ry — 0, h — ry
    /// (старий клієнт без курсу тягача), f — 0.
    /// </summary>
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
        var ry = Num("ry", 0);
        var f = (int)Math.Clamp(Math.Round(Num("f", 0)), 0, 7);
        return new FuraPose(Num("x", null), Num("z", null), Num("v", 0), c, k, Num("rx", null), Num("rz", null), ry, Num("h", ry), f);
    }

    // ---------- вид ----------

    /// <summary>Вид місця: його фура + колона й стіл. Глядачу — лише колона й стіл, без чужої дошки, замовлення й рейсу.</summary>
    public override object View(int? seat)
    {
        var rig = seat is { } s && s >= 0 && s < _rigs.Length ? _rigs[s] : null;
        var pose = rig?.Last;
        var result = rig?.Result;
        return new
        {
            stage = Stage,
            map = Map.Id,
            seat = rig is null ? null : seat,
            phase = rig?.Phase,
            trips = rig?.Trips ?? 0,
            board = rig is { Phase: Free }
                ? (OffersCol(rig) && ColOrder() is { } k
                    ? rig.Board.Select(x => OrderView(x)).Append(OrderView(k, board: true))
                    : rig.Board.Select(x => OrderView(x))).ToArray()
                : null,
            order = rig?.Order is { } o ? OrderView(o) : null,
            trip = rig is { Phase: Pickup or Haul } ? new
            {
                takenAt = rig.TakenAt,
                sec = (int)Math.Round(rig.Judge.TripSec),
                haulSec = (int)Math.Round(rig.Judge.HaulSec),
                driven = Math.Round(rig.Judge.HaulM / 1000, 2),
                minK = rig.Judge.MinK,
                auto = rig.Auto,
                rescue = rig.Judge.Rescue,
                violations = rig.Judge.Violations,
                voided = rig.Judge.Void,
            } : null,
            pose = pose is null ? null : new
            {
                x = pose.X, z = pose.Z, v = pose.V, c = pose.C, k = pose.K, rx = pose.Rx, rz = pose.Rz, ry = pose.Ry, h = pose.H, f = pose.F,
                at = rig!.LastAt,
            },
            result = result is null ? null : new
            {
                trip = result.Trip,
                cargo = result.Order.Cargo,
                from = result.Order.From,
                to = result.Order.To,
                km = result.Order.Km,
                driven = result.Driven,
                sec = result.Sec,
                minutes = result.Order.Minutes,
                k = result.K,
                onTime = result.OnTime,
                exact = result.Exact,
                auto = result.Auto,
                broken = result.K < FuraJudge.BrokenK,
                @base = result.Base,
                shards = Earn(result.Base),
                col = result.Col,
                bonus = Earn(result.Bonus),
            },
            cap = Earn(Econ.FuraDailyCap),
            convoy = Convoy(),
            col = ColView(),
        };
    }

    object[] Convoy()
    {
        var list = new List<object>(_rigs.Length);
        for (var s = 0; s < _rigs.Length; s++)
            if (_rigs[s] is { } r)
                list.Add(new { seat = s, nick = r.Nick, phase = r.Phase, cargo = r.Order?.Cargo, to = r.Order?.To, col = ColMember(r) is not null });
        return [.. list];
    }

    object? ColView() => _col is not { } col ? null : new
    {
        no = col.No,
        order = new
        {
            id = ColId, cargo = col.Order.Cargo, from = col.Order.From, to = col.Order.To, km = col.Order.Km,
            minutes = col.Order.Minutes, pay = Earn(col.Order.Base),
        },
        members = col.In.Select(m => m.Nick).ToArray(),
        docks = col.In.ToDictionary(m => m.Nick, m => m.Dock),
        firstAt = col.FirstAt,
        done = col.In.Where(m => m.DoneAt is not null).Select(m => new { nick = m.Nick, at = m.DoneAt }).ToArray(),
    };

    /// <summary>
    /// Замовлення у виді; колонне — з <c>id: "k"</c>. На дошці (<paramref name="board"/>) колонне без боксу — бокс кожному
    /// свій (<c>col.docks</c>); у <c>order</c> учасника — його бокс.
    /// </summary>
    object OrderView(FuraOrder o, bool board = false) => new
    {
        id = o.Col ? (object)ColId : o.Id,
        cargo = o.Cargo,
        from = o.From,
        dock = board ? null : o.Dock,
        to = o.To, km = o.Km, minutes = o.Minutes, pay = Earn(o.Base),
    };

    // ---------- перезапуск ----------

    /// <summary>
    /// Стіл переживає перезапуск сайту: інакше вічна партія тримала б деплой (<see cref="Rooms.Busy"/>) або переривалась.
    /// Знімок — колона й фури (свіжіші за гараж: у них і поза з Input).
    /// </summary>
    public override bool Resumable => true;

    sealed record Saved(int V, int ColNo, FuraConvoy? Col, long Tick, Dictionary<string, string> Rigs);

    public override string? Save()
    {
        var rigs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in _rigs)
            if (r is not null && Owned(r)) rigs[r.NickKey] = r.ToJson();
        return JsonSerializer.Serialize(new Saved(2, _colNo, _col, _tick, rigs), Json);
    }

    public override void Load(string json)
    {
        Saved? s;
        try { s = JsonSerializer.Deserialize<Saved>(json, Json); }
        catch (JsonException) { return; }
        if (s is null || s.V != 2) return;
        _colNo = Math.Max(1, s.ColNo);
        _col = s.Col is { Order: not null } c && Map.Place(c.Order.From) is not null ? c : null;
        _tick = s.Tick;
        for (var seat = 0; seat < _rigs.Length; seat++)
        {
            if (_rigs[seat] is not { } now || s.Rigs is null || !s.Rigs.TryGetValue(now.NickKey, out var rj)) continue;
            if (FuraRig.Parse(rj, Map) is not { } back) continue;
            back.Nick = now.Nick;
            back.NickKey = now.NickKey;
            back.Claim = now.Claim;
            EnsureBoard(back);
            _rigs[seat] = back;
            Keep(back);
        }
        _dirty = true;
    }
}

/// <summary>
/// Замовлення на дошці. <see cref="Base"/> — орієнтовна база плати (ціле 100 %, без бонусів), у виді — × Boost.
/// <see cref="Col"/> — колонне (<c>id: "k"</c>); у фурі учасника <see cref="Dock"/> — його бокс.
/// </summary>
public sealed record FuraOrder(int Id, string Cargo, string From, string Dock, string To, double Km, int Minutes, int Base, bool Col = false);

/// <summary>
/// Підсумок зданого рейсу: проїхано (км), час без пауз (с), ціле, бонуси й база плати (до × Boost); колонне — і бонус
/// колони (база, ⌈база × 0,15⌉), коли його вже нараховано.
/// </summary>
public sealed record FuraResult(int Trip, FuraOrder Order, double Driven, int Sec, int K, bool OnTime, bool Exact, bool Auto, int Base,
    bool Col = false, int Bonus = 0);
