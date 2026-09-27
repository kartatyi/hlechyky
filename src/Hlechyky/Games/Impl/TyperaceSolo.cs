using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Клавоперегони: тренування — той самий заїзд самому, з секундоміром, особистим рекордом і таблицею «знаків за
/// хвилину». Кімната одна на ніка й живе, доки на неї дивляться; партія не закінчується ніколи (як Гончарне коло):
/// заїзди крутяться всередині — <c>pick → ready → go → done → go …</c>. Spec: docs/games/specs/typerace.md §2.2.
/// </summary>
public sealed class TyperaceSolo : TyperaceRace
{
    public const string ActGo = "go", ActStop = "stop";
    /// <summary>Хвилина без жодного натиску — людина відійшла, назад до вибору (за столом тиша коротша, там інша причина).</summary>
    public const int IdleMs = 60_000;

    public override GameInfo Info { get; } = new(
        "typerace-solo", "Клавоперегони: тренування", "тренування клавоперегонів", GameGroup.Solo, 1, 1,
        TickMs: TickMs, Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Options: TyperaceOptions.All,
        Hint: "Розігрів для пальців: уривок, секундомір, особистий рекорд і таблиця «знаків за хвилину». Помилку виправ — інакше не поїдеш",
        Client: "typerace");

    int? _best;
    int _runs;
    bool _record;
    /// <summary>Останній заїзд — для збереження й підсумку після F5.</summary>
    (int Cpm, int Acc, int Wrong, long Ms, int Len)? _last;
    string? _lastTitle;

    public override void Configure(IReadOnlyDictionary<string, string> options) => TakeBank();

    protected override bool SendsFrames => false;

    /// <summary>У виборі трактор стоїть на старті: для траси це те саме лобі, лише без тексту.</summary>
    protected override bool LobbyNow => Phase == PhasePick;

    /// <summary>Чистий стан: вибір довжини й джерела, без тексту. Load (якщо є що) ляже вже поверх.</summary>
    public override void Start()
    {
        Phase = PhasePick;
        Length = TyperaceBank.Medium;
        Source = TyperaceBank.All;
        _best = null;
        _runs = 0;
        _record = false;
        _last = null;
        _lastTitle = null;
        Text = null;
        Src = null;
        Len = 0;
        Result = null;
        foreach (var r in Racers) r.Reset(r.Seat, false, null);
    }

    public override ActResult Act(int seat, string action, JsonElement payload) => action switch
    {
        ActGo => Go(payload),
        ActStop => Stop(),
        _ => base.Act(seat, action, payload),
    };

    ActResult Go(JsonElement payload)
    {
        if (Phase is PhaseReady or PhaseGo) return ActResult.Fail("Спершу дограй або натисни «Стоп»");
        var length = Length;
        var source = Source;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("length", out var l) && l.ValueKind != JsonValueKind.Null)
            {
                if (l.ValueKind != JsonValueKind.String || Array.IndexOf(TyperaceBank.Lengths, l.GetString()) < 0)
                    return ActResult.Fail("Такої довжини нема");
                length = l.GetString()!;
            }
            if (payload.TryGetProperty("source", out var s) && s.ValueKind != JsonValueKind.Null)
            {
                if (s.ValueKind != JsonValueKind.String || Array.IndexOf(TyperaceBank.Sources, s.GetString()) < 0)
                    return ActResult.Fail("Таких текстів нема");
                source = s.GetString()!;
            }
        }
        if (Bank.Empty) return ActResult.Fail(Typerace.NoTexts);
        Length = length;
        Source = source;
        _record = false;
        if (!BeginReady()) return ActResult.Fail(Typerace.NoTexts);
        // соло-кімната — реалтайм для каркаса: після Act він видів не шле, розішле найближчий тик (≤ 200 мс)
        ViewDirty = true;
        return ActResult.Done;
    }

    ActResult Stop()
    {
        if (Phase is not (PhaseReady or PhaseGo)) return ActResult.Fail("Зараз нема чого спиняти");
        ToPick();
        return ActResult.Done;
    }

    void ToPick()
    {
        Phase = PhasePick;
        Text = null;
        Src = null;
        Len = 0;
        Result = null;
        _record = false;
        foreach (var r in Racers) r.Reset(r.Seat, false, null);
        ViewDirty = true;
    }

    protected override void OnVerified(Racer r, DateTimeOffset now)
    {
        _runs++;
        var cpm = r.Cpm ?? 0;
        Ctx.Score(r.Seat, cpm);
        _record = _best is null || cpm > _best;
        if (_record) _best = cpm;
        AwardAchievements(r);
        Done(r, TyperaceLines.ForSolo(Ctx.Rng, cpm, _record));
    }

    protected override void OnFlagged(Racer r, DateTimeOffset now)
    {
        _runs++;
        _record = false;
        Done(r, $"Фініш є, але не зараховано: {TyperaceJudge.Reason(r.Flag)}. Спробуй ще — по-людськи.");
    }

    /// <summary>Тост на фініш соло не потрібен: підсумок заїзду з'являється тут-таки, і рекорд у ньому вже написано.</summary>
    protected override string FinishText(Racer r) => "";

    protected override string FlaggedText(Racer r) => "";

    void Done(Racer r, string say)
    {
        Phase = PhaseDone;
        _last = (r.Cpm ?? 0, r.Acc ?? 0, r.Wrong, r.Fin ?? 0, Len);
        _lastTitle = r.Flag is null ? TyperaceLines.Title(r.Cpm ?? 0) : null;
        Result = new Summary([r.Seat], [], say, true);
        ViewDirty = true;
    }

    protected override void OnRaceTick(DateTimeOffset now)
    {
        var r = Racers[0];
        if (!Moved && GoAt is { } g && (now - g).TotalMilliseconds >= IdleMs)
        {
            // хвилина тиші — людина відійшла; заїзд не рахуємо
            ToPick();
            return;
        }
        if (EndsAt is { } end && now >= end && r.Fin is null)
        {
            FillUnfinished(now);
            Phase = PhaseDone;
            _record = false;
            _lastTitle = null;
            Result = new Summary([0], [], TyperaceLines.ForSoloUnfinished(Percent(r)), false);
            ViewDirty = true;
        }
    }

    public override object View(int? seat)
    {
        var v = BaseView(PhasePick);
        v["me"] = new
        {
            best = _best,
            runs = _runs,
            isRecord = _record && Phase == PhaseDone,
            title = Phase == PhaseDone ? _lastTitle : null,
        };
        if (Bank is null || Bank.Empty) v["noTexts"] = true;
        return v;
    }

    // ---------------------------------------------------------------------------------------------
    // Збереження: налаштування, рекорд, кількість заїздів і останній заїзд. Сам заїзд не зберігається.
    // ---------------------------------------------------------------------------------------------

    public const int SaveVersion = 1;

    public override string? Save()
    {
        var o = new JsonObject
        {
            ["v"] = SaveVersion,
            ["length"] = Length,
            ["source"] = Source,
            ["best"] = _best,
            ["runs"] = _runs,
        };
        if (_last is { } l)
            o["last"] = new JsonObject { ["cpm"] = l.Cpm, ["acc"] = l.Acc, ["wrong"] = l.Wrong, ["ms"] = l.Ms, ["len"] = l.Len };
        return o.ToJsonString();
    }

    public override void Load(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (root is not JsonObject o || Int(o["v"]) != SaveVersion) return;
        if (o["length"] is JsonValue lv && lv.TryGetValue<string>(out var len) && Array.IndexOf(TyperaceBank.Lengths, len) >= 0) Length = len;
        if (o["source"] is JsonValue sv && sv.TryGetValue<string>(out var src) && Array.IndexOf(TyperaceBank.Sources, src) >= 0) Source = src;
        _best = Int(o["best"]) is { } b and >= 0 ? b : null;
        _runs = Int(o["runs"]) is { } n and >= 0 ? n : 0;
        if (o["last"] is JsonObject last && Int(last["cpm"]) is { } c && Int(last["len"]) is { } ln)
            _last = (c, Int(last["acc"]) ?? 0, Int(last["wrong"]) ?? 0, Int(last["ms"]) ?? 0, ln);
        Phase = PhasePick;
    }

    static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : n is JsonValue w && w.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
}
