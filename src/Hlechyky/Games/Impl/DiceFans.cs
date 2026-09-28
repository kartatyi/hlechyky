using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Шматок репліки Глека у виді «Під глеком»: адреса mp3 і скільки звучить.</summary>
public sealed record DiceClipView(string Url, double Seconds);

/// <summary>Репліка вголос: номер (клієнт грає кожну раз), текст і шматки, що звучать один за одним.</summary>
public sealed record DiceSpeechView(int Id, string Text, DiceClipView[] Parts);

/// <summary>
/// Що Глек каже вголос на розкритті (specs/dice.md «Прохід №3»). Сталі рядки: їх озвучують наперед на старті, тож
/// різниця в одну кому дала б інший хеш і кліп, якого в кеші нема. Ніки — у називному після тире: чужі імена ми не
/// відмінюємо, а рід не знаємо («Брехня! Загнув — Петро», а не «Петро загнула»… чи «загнув»).
/// </summary>
public static class DiceVoiceLines
{
    public static string Lie(string bidder) => $"Брехня! Загнув — {bidder}.";
    public static string Truth(string caller) => $"Правда! Кісточку віддає — {caller}.";
    public static string ExactHit(string caller) => $"Точно! Кісточку повертає — {caller}.";
    public static string ExactMiss(string caller) => $"Мимо! Кісточку віддає — {caller}.";
    public static string Out(string nick) => $"Без кісточок лишається — {nick}.";
    public static string Win(string nick) => $"Перемагає — {nick}! Браво!";

    public static IEnumerable<string> For(string nick) =>
        [Lie(nick), Truth(nick), ExactHit(nick), ExactMiss(nick), Out(nick), Win(nick)];
}

/// <summary>Звання партії, які йдуть у «сезон» (див. <see cref="DiceSeason"/>).</summary>
public static class DiceTitles
{
    public const string Bluff = "bluff", Sniper = "sniper", Nose = "nose", Sleepy = "sleepy", Fan = "fan";

    public static readonly (string Kind, string Icon, string Title)[] All =
    [
        (Bluff, "🤥", "Блефер тижня"),
        (Nose, "🕵", "Нюх на брехню"),
        (Sniper, "🎯", "Снайпер «Точно!»"),
        (Fan, "👃", "Нюх вболівальника"),
        (Sleepy, "😴", "Соня"),
    ];
}

public sealed partial class Dice
{
    /// <summary>Скільки вердикт може запізнитись: пізніше вже трусять глеки наступного раунду.</summary>
    public const int VoiceGraceMs = 1500;
    /// <summary>Скільки вболівальників показуємо в таблиці «нюх вболівальника».</summary>
    public const int FansShown = 6;

    // ---------- голос Глека ----------
    string _voiceName = "ostap";
    IDotepyVoice _voice = DotepyNoVoice.Instance;
    DiceSpeechView? _speech;
    int _speechId;
    string[]? _want;
    DateTimeOffset _wantUntil;

    bool VoiceOn => _voice.Enabled;
    public DiceSpeechView? Speech => _speech;

    void ConfigureVoice(IReadOnlyDictionary<string, string> options) =>
        _voiceName = options.GetValueOrDefault("voice") is "polina" or "none" ? options["voice"] : "ostap";

    void StartVoice()
    {
        _speech = null;
        _want = null;
        _voice = DotepyNoVoice.Instance;
        if (_voiceName == "none") return;
        try
        {
            if (Ctx.Services.GetService<IDotepyVoice>() is { } v && v.Enabled) _voice = v;
        }
        catch (Exception) { /* голос — чужий код; не вийшло — граємо текстом */ }
        if (!VoiceOn) return;
        var lines = new List<string>();
        for (var s = 0; s < DiceCore.MaxSeats; s++)
            if (_core.Dealt[s]) lines.AddRange(DiceVoiceLines.For(Nick(s)));
        try { _voice.Prepare(_voiceName, lines); }
        catch (Exception) { /* без голосу */ }
    }

    DotepyClip? Clip(string text)
    {
        try { return _voice.Ready(_voiceName, text); }
        catch (Exception) { return null; }
    }

    /// <summary>Глек скаже це, щойно кліпи доспіють, але не пізніше ніж за <see cref="VoiceGraceMs"/>. Гра не чекає.</summary>
    void Speak(string[] parts, bool final = false)
    {
        _want = null;
        if (!VoiceOn || parts.Length == 0) return;
        _want = parts;
        _wantUntil = final ? Ctx.Clock.UtcNow : Ctx.Clock.UtcNow.AddMilliseconds(VoiceGraceMs);
        TrySpeak();
    }

    /// <summary>З тика (під замком кімнати): лише словник готових кліпів у пам'яті, жодного диска.</summary>
    void TrySpeak()
    {
        if (_want is not { } parts) return;
        var clips = new List<DiceClipView>(parts.Length);
        var text = new List<string>(parts.Length);
        foreach (var p in parts)
            if (Clip(p) is { } c)
            {
                clips.Add(new DiceClipView(c.Url, Math.Round(c.Seconds, 2)));
                text.Add(p);
            }
        if (clips.Count < parts.Length && Ctx.Clock.UtcNow < _wantUntil) return;
        _want = null;
        if (clips.Count == 0) return;
        _speech = new DiceSpeechView(++_speechId, string.Join(' ', text), [.. clips]);
        _dirty = true;
    }

    /// <summary>Вердикт розкриття вголос: хто загнув (чи хто даремно не повірив), і хто лишився без кісточок.</summary>
    void VoiceReveal(DiceOutcome o)
    {
        if (!VoiceOn) return;
        var parts = new List<string>(2);
        if (o.Kind == "exact")
            parts.Add(o.Gainer is not null ? DiceVoiceLines.ExactHit(Nick(o.Caller)) : DiceVoiceLines.ExactMiss(Nick(o.Caller)));
        else if (o.Loser == o.Bid.Seat) parts.Add(DiceVoiceLines.Lie(Nick(o.Bid.Seat)));
        else parts.Add(DiceVoiceLines.Truth(Nick(o.Caller)));
        if (o.Out && o.Loser is { } l) parts.Add(DiceVoiceLines.Out(Nick(l)));
        Speak([.. parts]);
    }

    // ---------- вболівальники ----------

    /// <summary>Ставка вболівальника на ставку, що лежить на столі (ключ — раунд і номер ставки).</summary>
    sealed record FanBet(string Nick, int Key, bool Truth);
    sealed class FanScore(string nick)
    {
        public string Nick { get; } = nick;
        public int Hits;
        public int Tries;
    }

    readonly Dictionary<string, FanBet> _bets = new(StringComparer.Ordinal);
    readonly Dictionary<string, FanScore> _fans = new(StringComparer.Ordinal);
    /// <summary>Хто що ставив на розкриту ставку — для рядка під розкриттям.</summary>
    List<(string Nick, bool Truth, bool Hit)> _settled = [];

    /// <summary>Ставка, на яку зараз можна поставити: раунд × 1000 + номер ставки в раунді.</summary>
    int BetKey => _core.Round * 1000 + _core.History.Count;

    void ResetFans()
    {
        _bets.Clear();
        _fans.Clear();
        _settled = [];
    }

    /// <summary>
    /// «Правда/брехня» від того, хто не грає: вибулого (з місця) чи глядача (HTTP, <see cref="DiceFans"/>). Правда —
    /// «на столі щонайменше стільки», тобто ставка витримає «Брешеш!». Можна передумати до розкриття; нова ставка на столі
    /// — нове питання.
    /// </summary>
    public ActResult Bet(string nick, int? seat, bool truth)
    {
        if (seat is { } s && s is >= 0 and < DiceCore.MaxSeats && _core.Dealt[s] && _core.Alive[s])
            return ActResult.Fail("Ти ще граєш — став кісточками, а не словом");
        if (_phase != DicePhase.Bid || _core.Bid is null) return ActResult.Fail("Ставити можна, коли на столі є ставка");
        var key = Auth.NickKey(nick);
        _bets[key] = new FanBet(nick, BetKey, truth);
        _dirty = true;
        return ActResult.Accept(truth ? "Ставиш на правду" : "Ставиш на брехню");
    }

    ActResult BetAct(int seat, JsonElement payload)
    {
        var truth = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("truth", out var t)
            ? t.ValueKind == JsonValueKind.True
            : payload.ValueKind == JsonValueKind.True;
        return Bet(Ctx.NickOf(seat) ?? Nick(seat), seat, truth);
    }

    /// <summary>Розкриття: влучні ставки на розкриту ставку — у таблицю, решта згоряє.</summary>
    void SettleBets(DiceOutcome o)
    {
        _settled = [];
        if (_bets.Count == 0) return;
        var key = BetKey;
        var truth = o.Count >= o.Bid.Q;
        foreach (var (k, b) in _bets)
        {
            if (b.Key != key) continue;
            if (!_fans.TryGetValue(k, out var f)) _fans[k] = f = new FanScore(b.Nick);
            var hit = b.Truth == truth;
            f.Tries++;
            if (hit) f.Hits++;
            _settled.Add((b.Nick, b.Truth, hit));
        }
        _bets.Clear();
    }

    object? BetsView()
    {
        if (_phase != DicePhase.Bid || _bets.Count == 0) return null;
        int key = BetKey, yes = 0, no = 0;
        foreach (var b in _bets.Values)
            if (b.Key == key) { if (b.Truth) yes++; else no++; }
        return yes + no == 0 ? null : new { key, truth = yes, lie = no };
    }

    object[]? FansView()
    {
        if (_fans.Count == 0) return null;
        return _fans.Values.Where(f => f.Tries > 0).OrderByDescending(f => f.Hits).ThenBy(f => f.Tries).Take(FansShown)
            .Select(f => (object)new { nick = f.Nick, hits = f.Hits, tries = f.Tries }).ToArray();
    }

    /// <summary>
    /// Поле <c>fan</c> виду: <c>say</c> — репліка Глека вголос; <c>bets</c> — скільки ставлять на ставку на столі;
    /// <c>settled</c> — хто що ставив на розкриту (лише в розкритті); <c>table</c> — «нюх вболівальника» партії.
    /// </summary>
    object? FanView()
    {
        var bets = BetsView();
        var table = FansView();
        var settled = _phase is DicePhase.Reveal && _settled.Count > 0
            ? _settled.Select(f => new { nick = f.Nick, truth = f.Truth, hit = f.Hit }).ToArray() : null;
        if (_speech is null && bets is null && table is null && settled is null) return null;
        return new { say = _speech, bets, settled, table };
    }

    FanScore? BestFan()
    {
        FanScore? best = null;
        foreach (var f in _fans.Values)
            if (f.Hits >= 2 && (best is null || f.Hits > best.Hits || (f.Hits == best.Hits && f.Tries < best.Tries))) best = f;
        return best;
    }

    // ---------- сезон ----------

    /// <summary>Звання дограної партії — у «сезон» (фоном: під замком кімнати жодної бази).</summary>
    void RecordSeason()
    {
        DiceSeason? season;
        try { season = Ctx.Services.GetService<DiceSeason>(); }
        catch (Exception) { season = null; }
        if (season is null) return;
        var titles = new List<(string Kind, string Nick)>(5);
        if (_bluff.Seat >= 0) titles.Add((DiceTitles.Bluff, Nick(_bluff.Seat)));
        if (Best(_exacts, 1) is { } sn) titles.Add((DiceTitles.Sniper, Nick(sn)));
        if (Best(_catches, 2) is { } nose) titles.Add((DiceTitles.Nose, Nick(nose)));
        if (Best(_timeouts, 2) is { } sleepy) titles.Add((DiceTitles.Sleepy, Nick(sleepy)));
        if (BestFan() is { } fan) titles.Add((DiceTitles.Fan, fan.Nick));
        season.Record(titles, Ctx.Clock.UtcNow);
    }
}
