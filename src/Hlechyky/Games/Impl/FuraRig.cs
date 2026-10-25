using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Фура» — рейс одного гравця «Чумаків» (specs/fura.md, «Етап 2 — колона»): фаза, дошка, замовлення, суддя, остання поза,
/// підсумок і скільки рейсів зроблено. Належить ніку, не столу: живе в <see cref="FuraGarage"/> під
/// <c>fura:rig:&lt;nickKey&gt;</c>, стіл бере її, коли нік сідає, і віддає назад після кожного Act і в OnLeave.
/// Стан — публічні властивості (як у <see cref="FuraJudge"/>): у JSON іде як є; [JsonIgnore] — лише те, що знає стіл.
/// </summary>
public sealed class FuraRig
{
    public int V { get; set; } = 2;
    public string Phase { get; set; } = Fura.Free;
    /// <summary>Рейсів зроблено (зданих, хай навіть «усе розбито»). Номер наступного — +1, наскрізь за ніком.</summary>
    public int Trips { get; set; }
    public string BoardDay { get; set; } = "";
    public List<FuraOrder> Board { get; set; } = [];
    public FuraOrder? Order { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public bool Auto { get; set; }
    public FuraJudge Judge { get; set; } = new();
    public FuraPose? Last { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public FuraResult? Result { get; set; }
    /// <summary>Колонне замовлення: за яким столом і в якій колоні (номер) узято. Інший стіл — везе як своє, без бонусу.</summary>
    public string? ColRoom { get; set; }
    public int ColNo { get; set; }

    [JsonIgnore] public string Nick { get; set; } = "";
    [JsonIgnore] public string NickKey { get; set; } = "";
    /// <summary>Чий це рейс зараз: номер «заявки» в гаражі. Інший стіл забрав фуру — цей її більше не судить.</summary>
    [JsonIgnore] public long Claim { get; set; }
    /// <summary>Коли востаннє віддано в гараж (поза пишеться не частіше за <see cref="Fura.KeepPoseSec"/>).</summary>
    [JsonIgnore] public DateTimeOffset KeptAt { get; set; }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Фура з JSON (свого або старого соло-запису етапу 1 — поля ті самі). null — лише коли JSON нечитний зовсім. Рейс, якого
    /// мапа не знає (битий бокс, вантаж, місце), скасовується → free; <see cref="Trips"/> лишається: інакше номери рейсів
    /// пішли б назад і повторили вже оплачені ref-и <c>fura:&lt;нік&gt;:&lt;N&gt;</c> — гравець возив би задарма.
    /// </summary>
    public static FuraRig? Parse(string? json, FuraMap map)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        FuraRig? r;
        try { r = JsonSerializer.Deserialize<FuraRig>(json, Json); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        if (r is null) return null;
        r.Trips = Math.Max(0, r.Trips);
        bool Valid(FuraOrder? o) => o is { Cargo: not null, From: not null, To: not null, Dock: not null }
            && map.CargoOf(o.Cargo) is not null && map.Place(o.To) is not null && map.Place(o.From)?.Dock(o.Dock) is not null;
        r.Board = (r.Board ?? []).ToList();
        if (!r.Board.All(Valid)) r.Board = [];
        var broken = r.Phase switch
        {
            Fura.Free => false,
            Fura.Pickup or Fura.Haul => !Valid(r.Order),
            Fura.Done => r.Result is null || !Valid(r.Result.Order),
            _ => true,
        };
        if (broken)
        {
            r.Phase = Fura.Free;
            r.Board = [];   // нова — з першого ж EnsureBoard
            r.Auto = false;
            r.Judge = new();
        }
        if (r.Phase == Fura.Free) r.Order = null;
        if (r.Phase != Fura.Done) r.Result = null;
        r.Judge ??= new();
        r.BoardDay ??= "";
        r.V = 2;
        return r;
    }

    /// <summary>Скільки рейсів записано в JSON, навіть коли сама фура з нього не читається (0 — не дістати).</summary>
    public static int TripsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("trips", out var t) && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var n) ? Math.Max(0, n) : 0;
        }
        catch (JsonException) { return 0; }
    }
}

/// <summary>
/// Гараж фур: один на сервер. Тримає останній JSON кожної фури в пам'яті (стіл, що сів пізніше, бере свіже, навіть якщо
/// база ще не дописала) і пише в <see cref="IGameStore"/> з пулу потоків — ніколи з-під замка кімнати (як
/// <see cref="RallyLaps"/>). Ще знає, який стіл зараз «судить» нік: <see cref="Claim"/> віддає фуру новому столу, і старий
/// своїми діями її вже не перепише.
/// </summary>
public sealed class FuraGarage
{
    public static string Key(string nickKey) => $"fura:rig:{nickKey}";
    /// <summary>Старий соло-запис етапу 1 (<see cref="Game.SoloKey"/> типовий: <c>fura:&lt;nickKey&gt;</c>) — для міграції.</summary>
    public static string OldKey(string nickKey) => $"fura:{nickKey}";

    readonly IGameStore _store;
    readonly Action<Action> _defer;
    readonly ILogger? _log;
    readonly object _lock = new();
    readonly object _writeLock = new();
    readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _owners = new(StringComparer.Ordinal);
    /// <summary>Фури, чий останній запис у базу впав: їх дописує наступний <see cref="Save"/> (будь-чий).</summary>
    readonly HashSet<string> _unsaved = new(StringComparer.Ordinal);
    long _claims;
    int _failures;

    /// <param name="defer">Як виконати запис у сховище: типово — пул потоків; тести передають «одразу».</param>
    public FuraGarage(IGameStore store, Action<Action>? defer = null, ILogger? log = null)
    {
        _store = store;
        _defer = defer ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        _log = log;
    }

    /// <summary>Скільки разів запис у базу впав (за життя процесу).</summary>
    public int Failures => Volatile.Read(ref _failures);

    /// <summary>Скільки фур зараз лише в пам'яті: запис у базу впав і ще не повторений.</summary>
    public int Unsaved
    {
        get { lock (_lock) return _unsaved.Count; }
    }

    /// <summary>Свіжий JSON фури: з пам'яті, інакше з бази. null — нема.</summary>
    public string? Load(string nickKey)
    {
        lock (_lock)
            if (_latest.TryGetValue(nickKey, out var json)) return json;
        return _store.LoadState(Key(nickKey));
    }

    /// <summary>Старий соло-запис етапу 1 (не видаляється).</summary>
    public string? LoadOld(string nickKey) => _store.LoadState(OldKey(nickKey));

    /// <summary>Стіл бере фуру ніка: попередня заявка (інший стіл) більше не чинна.</summary>
    public long Claim(string nickKey)
    {
        lock (_lock)
        {
            var c = ++_claims;
            _owners[nickKey] = c;
            return c;
        }
    }

    public bool Owns(string nickKey, long claim)
    {
        lock (_lock) return _owners.TryGetValue(nickKey, out var c) && c == claim;
    }

    /// <summary>Стіл відпускає фуру (нік встав). Чужу заявку не чіпає.</summary>
    public void Release(string nickKey, long claim)
    {
        lock (_lock)
            if (_owners.TryGetValue(nickKey, out var c) && c == claim) _owners.Remove(nickKey);
    }

    /// <summary>Записати фуру — лише якщо заявка ще чинна. false — фуру вже забрав інший стіл, нічого не записано.</summary>
    public bool Save(string nickKey, long claim, string json)
    {
        string[] retry;
        lock (_lock)
        {
            if (!_owners.TryGetValue(nickKey, out var c) || c != claim) return false;
            _latest[nickKey] = json;
            retry = _unsaved.Count == 0 ? [] : [.. _unsaved.Where(k => k != nickKey)];
        }
        _defer(() => Flush(nickKey));
        foreach (var k in retry) _defer(() => Flush(k));
        return true;
    }

    /// <summary>Запис у сховище: знімок береться вже під замком запису, тож останній запис — завжди найсвіжіший.</summary>
    void Flush(string nickKey)
    {
        lock (_writeLock)
        {
            string? json;
            lock (_lock) _latest.TryGetValue(nickKey, out json);
            if (json is null) return;
            try
            {
                _store.SaveState(Key(nickKey), json);
                lock (_lock) _unsaved.Remove(nickKey);
            }
            catch (Exception ex)
            {
                // база впала — фура лишається в пам'яті, наступний Save (будь-чий) спробує ще раз
                bool first;
                lock (_lock) first = _unsaved.Add(nickKey);
                Interlocked.Increment(ref _failures);
                if (first) _log?.LogWarning(ex, "Чумаки: фуру {Nick} не записано в базу — повторю наступним записом", nickKey);
            }
        }
    }
}
