using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Фура» — рейс одного гравця «Чумаків» (specs/fura.md, «Етап 2 — колона»): фаза, дошка, замовлення, суддя, остання поза,
/// підсумок і скільки рейсів зроблено. Належить ніку, не столу: живе в <see cref="FuraGarage"/> під
/// <c>fura:rig:&lt;nickKey&gt;</c>, стіл бере її, коли нік сідає, і віддає назад після кожного Act і в OnLeave.
/// Стан — публічні властивості (як у <see cref="FuraJudge"/>): у JSON іде як є; [JsonIgnore] — лише те, що знає стіл.
/// <para>
/// Мапи (хвиля D): поза й рейс — свої на кожну мапу, <see cref="Trips"/> — спільний (від нього номери рейсів і ref-и
/// нагород). Запис один: стан краю — зверху, як і був (старий запис без мап — це край; старий код після відкату читає
/// край як є), стан інших мап — у полі <c>maps: {id: стан}</c>. Об'єкт у пам'яті — фура однієї мапи (<see cref="MapId"/>),
/// стани решти лежать у <see cref="Elsewhere"/> як є, до повернення на них.
/// </para>
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
    /// <summary>
    /// Пози, що прийшли за останні тики (A5): у кадр привидів ідуть УСІ, що прийшли з минулого тика, з часом і номером
    /// відправника — не лише остання. Пам'ять, не гараж; найстаріші зрізаються (<see cref="Fura.SentKeep"/>).
    /// </summary>
    [JsonIgnore] public List<FuraSent> Sent { get; } = [];
    /// <summary>Номер для пози старого клієнта без <c>n</c> (сервер нумерує сам).</summary>
    [JsonIgnore] public int AutoN { get; set; }
    /// <summary>Гараж водія (0.0.6): досвід, куплене, обране — окремий запис <see cref="FuraCareer.Key"/>, не в JSON фури.</summary>
    [JsonIgnore] public FuraCareer? Career { get; set; }
    /// <summary>Мапа, на якій ця фура (стіл, що її взяв). У JSON — місцем стану: край зверху, решта в <c>maps</c>.</summary>
    [JsonIgnore] public string MapId { get; set; } = FuraMap.DefaultId;
    /// <summary>Стани фури на інших мапах (id → JSON без <c>trips</c>), як прочитано: пишуться назад незмінними.</summary>
    [JsonIgnore] public Dictionary<string, JsonObject> Elsewhere { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Поле запису зі станами не-крайових мап.</summary>
    public const string MapsField = "maps";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Увесь запис: стан цієї мапи на своєму місці, решта — з <see cref="Elsewhere"/>, <c>trips</c> — зверху.</summary>
    public string ToJson()
    {
        var self = JsonSerializer.SerializeToNode(this, Json)!.AsObject();
        JsonObject root;
        if (MapId == FuraMap.DefaultId) root = self;
        else
        {
            root = Elsewhere.TryGetValue(FuraMap.DefaultId, out var kray) ? kray.DeepClone().AsObject() : [];
            root["v"] = V;
            root["trips"] = Trips;
            self.Remove("trips");
        }
        var maps = new JsonObject();
        foreach (var (id, state) in Elsewhere)
            if (id != FuraMap.DefaultId) maps[id] = state.DeepClone();
        if (MapId != FuraMap.DefaultId) maps[MapId] = self;
        root.Remove(MapsField);
        if (maps.Count > 0) root[MapsField] = maps;
        return root.ToJsonString(Json);
    }

    /// <summary>
    /// Фура мапи <paramref name="map"/> з JSON (свого або старого соло-запису етапу 1 — поля ті самі). null — лише коли JSON
    /// нечитний зовсім. На цій мапі ще не їздив — нова фура в депо з тими самими <see cref="Trips"/>; стани інших мап —
    /// у <see cref="Elsewhere"/>. Рейс, якого мапа не знає (битий бокс, вантаж, місце), скасовується → free; <see cref="Trips"/>
    /// лишається: інакше номери рейсів пішли б назад і повторили вже оплачені ref-и <c>fura:&lt;нік&gt;:&lt;N&gt;</c> —
    /// гравець возив би задарма.
    /// </summary>
    public static FuraRig? Parse(string? json, FuraMap map)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        FuraRig? r;
        var elsewhere = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            var kray = root.DeepClone().AsObject();
            var maps = kray[MapsField] as JsonObject;
            kray.Remove(MapsField);
            JsonObject? mine = null;
            if (map.Id == FuraMap.DefaultId) mine = kray;
            else elsewhere[FuraMap.DefaultId] = kray;
            foreach (var (id, state) in maps ?? [])
            {
                if (state is not JsonObject o || id == FuraMap.DefaultId) continue;
                if (id == map.Id) mine = o.DeepClone().AsObject();
                else elsewhere[id] = o.DeepClone().AsObject();
            }
            if (map.Id == FuraMap.DefaultId) r = mine!.Deserialize<FuraRig>(Json);
            else
            {
                // стан цієї мапи битий — нова фура в депо, а край і решта мап лишаються як були
                try { r = mine?.Deserialize<FuraRig>(Json) ?? new FuraRig(); }
                catch (JsonException) { r = new FuraRig(); }
                // рейси — спільні, лежать зверху
                r!.Trips = kray["trips"] is JsonValue t && t.TryGetValue<int>(out var n) ? n : 0;
            }
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (InvalidOperationException) { return null; }   // JsonNode не того типу
        if (r is null) return null;
        r.MapId = map.Id;
        r.Elsewhere = elsewhere;
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
/// своїми діями її вже не перепише. Гараж водія (0.0.6, <see cref="FuraCareer"/>) живе тут само під своїм ключем і тією самою
/// заявкою: пишеться рідко (рейс, покупка), тож поза раз на 5 с його не переписує.
/// </summary>
public sealed class FuraGarage : INickAdopter
{
    public static string Key(string nickKey) => $"fura:rig:{nickKey}";
    /// <summary>Старий соло-запис етапу 1 (<see cref="Game.SoloKey"/> типовий: <c>fura:&lt;nickKey&gt;</c>) — для міграції.</summary>
    public static string OldKey(string nickKey) => $"fura:{nickKey}";

    readonly IGameStore _store;
    readonly Action<Action> _defer;
    readonly ILogger? _log;
    readonly object _lock = new();
    readonly object _writeLock = new();
    /// <summary>Ключ сховища (<see cref="Key"/> чи <see cref="FuraCareer.Key"/>) → останній JSON.</summary>
    readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _owners = new(StringComparer.Ordinal);
    /// <summary>Ключі, чий останній запис у базу впав: їх дописує наступний <see cref="Save"/> (будь-чий).</summary>
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
    public string? Load(string nickKey) => Get(Key(nickKey));

    /// <summary>Свіжий JSON гаража водія (<see cref="FuraCareer"/>): з пам'яті, інакше з бази. null — нема.</summary>
    public string? LoadCareer(string nickKey) => Get(FuraCareer.Key(nickKey));

    string? Get(string key)
    {
        lock (_lock)
            if (_latest.TryGetValue(key, out var json)) return json;
        return _store.LoadState(key);
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
    public bool Save(string nickKey, long claim, string json) => Put(nickKey, Key(nickKey), claim, json);

    /// <summary>Записати гараж водія — за тією самою заявкою, що й фуру (стіл, що забрав нік, забирає і його гараж).</summary>
    public bool SaveCareer(string nickKey, long claim, string json) => Put(nickKey, FuraCareer.Key(nickKey), claim, json);

    bool Put(string nickKey, string key, long claim, string json)
    {
        string[] retry;
        lock (_lock)
        {
            if (!_owners.TryGetValue(nickKey, out var c) || c != claim) return false;
            _latest[key] = json;
            retry = _unsaved.Count == 0 ? [] : [.. _unsaved.Where(k => k != key)];
        }
        _defer(() => Flush(key));
        foreach (var k in retry) _defer(() => Flush(k));
        return true;
    }

    /// <summary>
    /// Гість «гість Вася» зробив акаунт «Вася» (<see cref="Accounts.Adopt"/>): ДВ і фура гостя — акаунту, без дублів.
    /// Фура акаунта є — лишається вона, гостьова йде в <c>lost:</c>; нема — переїжджає гостьова (з її рейсами). ДВ гостя
    /// (гараж, а без нього — ретро за його рейси) додається до ДВ акаунта (а без гаража — до ретро за рейси акаунта); номер
    /// останнього зарахованого рейсу — тієї фури, якою акаунт поїде далі. Гостьові ключі стираються: вдруге не додасться.
    /// Хтось із двох зараз за кермом (заявка стола) — не чіпаємо: стіл однаково перепише своє.
    /// </summary>
    public void Adopt(string guestNick, string nick)
    {
        var (g, a) = (Rooms.NickKey(guestNick), Rooms.NickKey(nick));
        if (g.Length == 0 || g == a) return;
        lock (_lock)
            if (_owners.ContainsKey(g) || _owners.ContainsKey(a)) return;
        var book = FuraBook.Load();
        var (gRig, aRig) = (Get(Key(g)), Get(Key(a)));
        var gCar = FuraCareer.Parse(Get(FuraCareer.Key(g)), book) ?? (gRig is null ? null : FuraCareer.Retro(TripsOf(gRig), book));
        if (gRig is null && gCar is null) return;
        var aCar = FuraCareer.Parse(Get(FuraCareer.Key(a)), book) ?? (aRig is null ? null : FuraCareer.Retro(TripsOf(aRig), book));
        if (gCar is not null)
        {
            if (aCar is null) aCar = gCar;
            else
            {
                aCar.Xp += gCar.Xp;
                if (aRig is null) aCar.LastTrip = gCar.LastTrip;   // поїде гостьовою фурою — її рейси вже зараховані
            }
            Write(FuraCareer.Key(a), aCar.ToJson());
        }
        if (gRig is not null)
        {
            if (aRig is null) Write(Key(a), gRig);
            else _store.SaveState($"lost:{Key(a)}:{DateTimeOffset.UtcNow:O}", gRig);
        }
        Drop(Key(g));
        Drop(FuraCareer.Key(g));
    }

    void Write(string key, string json)
    {
        lock (_lock) _latest[key] = json;
        _store.SaveState(key, json);
    }

    void Drop(string key)
    {
        lock (_lock) _latest.Remove(key);
        _store.DeleteState(key);
    }

    static int TripsOf(string rigJson)
    {
        try
        {
            using var d = JsonDocument.Parse(rigJson);
            return d.RootElement.TryGetProperty("trips", out var t) && t.TryGetInt32(out var n) ? n : 0;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>Запис у сховище: знімок береться вже під замком запису, тож останній запис — завжди найсвіжіший.</summary>
    void Flush(string key)
    {
        lock (_writeLock)
        {
            string? json;
            lock (_lock) _latest.TryGetValue(key, out json);
            if (json is null) return;
            try
            {
                _store.SaveState(key, json);
                lock (_lock) _unsaved.Remove(key);
            }
            catch (Exception ex)
            {
                // база впала — запис лишається в пам'яті, наступний Save (будь-чий) спробує ще раз
                bool first;
                lock (_lock) first = _unsaved.Add(key);
                Interlocked.Increment(ref _failures);
                if (first) _log?.LogWarning(ex, "Чумаки: {Key} не записано в базу — повторю наступним записом", key);
            }
        }
    }
}
