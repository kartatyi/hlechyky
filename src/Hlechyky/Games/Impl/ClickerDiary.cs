using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Щоденник гончаря — бік кола (сервіс і таблиці — <see cref="ClickerDiaryService"/>). Після кожної дії:
/// <list type="bullet">
/// <item>години — секунди біля кола (лише відрізки між справжніми діями, не довші за <see cref="DiaryActiveGap"/>), а ще
/// прирости виробів, глеків з полиці й кліків за цей відрізок;</item>
/// <item>знімок дня — не частіше за <see cref="ClickerDiaryOptions.SnapshotEvery"/>, а ще на новий день і коли коло зникає
/// з очей (дія <c>sight</c>): так у кінці дня в щоденнику стан на останню гру.</item>
/// </list>
/// Опорні лічильники в пам'яті кімнати й не зберігаються: після перезапуску перша дія лише ставить опору.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Довша пауза між діями — це вже не гра: відрізок не рахуємо (і вироби, що наліпили підмайстри, теж).</summary>
    public static readonly TimeSpan DiaryActiveGap = TimeSpan.FromSeconds(60);

    /// <summary>Версія знімка — щоб клієнт і відновлені рядки знали, які поля чекати.</summary>
    public const int DiaryVersion = 1;

    ClickerDiaryService? _diarySvc;
    bool _diaryLooked;
    DateTimeOffset _dHandsAt, _dSnapAt;
    string _dDay = "", _dClicksDay = "";
    long _dFormed = -1, _dGrabbed = -1, _dClicks = -1;

    ClickerDiaryService? DiarySvc()
    {
        if (!_diaryLooked) { _diarySvc = Ctx.Services.GetService<ClickerDiaryService>(); _diaryLooked = true; }
        return _diarySvc;
    }

    static readonly JsonSerializerOptions DiaryJson = new(JsonSerializerDefaults.Web);

    void DiaryAfterAct(string action, bool quiet, DateTimeOffset now)
    {
        if (DiarySvc() is not { Enabled: true } svc || Ctx.NickOf(0) is not { Length: > 0 } nick) return;
        var leaving = action == "sight";
        if (quiet && !leaving) return;
        DiaryHours(svc, nick, now);
        var day = Days.Of(now);
        if (leaving || day != _dDay || now - _dSnapAt >= svc.O.SnapshotEvery)
        {
            _dDay = day;
            _dSnapAt = now;
            svc.Put(nick, day, now, JsonSerializer.Serialize(DiarySnapshot(now), DiaryJson));
        }
    }

    void DiaryHours(ClickerDiaryService svc, string nick, DateTimeOffset now)
    {
        // Кліки дня скидаються опівночі (ClickerTitles.cs) — тоді й опора з нуля.
        var clicks = _tClicks;
        if (_dClicksDay != _tDay) { _dClicksDay = _tDay; if (_dClicks > clicks) _dClicks = 0; }
        var gap = now - _dHandsAt;
        var playing = _dFormed >= 0 && gap > TimeSpan.Zero && gap <= DiaryActiveGap;
        if (playing)
            svc.AddHour(nick, now, gap.TotalSeconds, Math.Max(0, _formed - _dFormed), Math.Max(0, _grabbed - _dGrabbed), Math.Max(0, clicks - _dClicks));
        _dHandsAt = now;
        _dFormed = _formed;
        _dGrabbed = _grabbed;
        _dClicks = clicks;
    }

    /// <summary>Знімок гончаря для щоденника: усе, з чого потім малюються криві й рахуються поради.</summary>
    object DiarySnapshot(DateTimeOffset now) => new
    {
        v = DiaryVersion,
        at = now,
        total = _total,
        pots = _pots,
        dayStart = _tDayStart,
        dayClicks = _tClicks,
        // ремесло
        formed = _formed,
        fired = new SortedDictionary<string, long>(_firedBy, StringComparer.Ordinal),
        clay = _clay,
        clayFormed = new SortedDictionary<string, long>(_clayFormed, StringComparer.Ordinal),
        kiln = new { batches = _kilnBatches, auto = _kilnAuto },
        // біля кола
        grabbed = _grabbed,
        streak = _fallStreak,
        caught = _caught,
        goldenMissed = _goldenMissed,
        lucky = _lucky,
        // прогрес
        levels = new SortedDictionary<string, int>(_levels, StringComparer.Ordinal),
        marks = _marks.Count,
        styles = _styles.Order(StringComparer.Ordinal).ToList(),
        firings = _firings,
        stamps = _stamps,
        stampsFree = FreeStamps,
        relics = new SortedDictionary<string, int>(_relics, StringComparer.Ordinal),
        slava = _slava,
        shipsFull = _shipsFull,
        toloka = new { built = _tBuilt.Order(StringComparer.Ordinal).ToList(), stage = _tStage },
        album = new
        {
            open = AlbumOpenCount(_albumCells),
            world = AlbumWorldOpen(_albumCells),
            stars = AlbumStarCount(_albumStars),
            stove = _stove.Count,
            finds = _finds,
        },
        fairLevels = _mktRepLevels,
        guestLevels = _gLevelSum,
        sold = new { fair = _mktDelivered, guests = _gDelivered, guild = _guildGiven },
        // множники «до всього» поскладниково — звідки береться кожен відсоток
        mult = new
        {
            all = AllMult,
            stamps = StampMult,
            styles = 1 + StyleBonus * _styles.Count,
            clayLevels = Math.Pow(1.25, Level("clay")),
            house = HouseAllMult,
            album = AlbumAllMult,
            fair = FairAllMult,
            guild = GuildAllMult,
            guests = GuestsAllMult,
            toloka = TolokaAllMult,
            passivePerk = 1 + Perk(MarkEffect.Passive),
        },
        passive = PassiveBase,
        playMinute = PlayMinute,
        // модель години гри (ClickerAdvisor.cs): поточна глина й кожна своя — звідки гроші біля кола й без гравця
        hour = DiaryHour(HourModel()),
        clays = ClayModels().ToDictionary(c => c.Key.Length == 0 ? "plain" : c.Key, c => new { level = c.Level, play = c.Hour.Play, idle = c.Hour.Idle, work = c.Work }),
    };

    static object DiaryHour(ClickerHour h) => new { passive = h.Passive, click = h.Click, fall = h.Fall, eye = h.Eye, merchant = h.Merchant, idle = h.Idle, play = h.Play };
}
