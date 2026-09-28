using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Шматок репліки ведучого у виді: адреса mp3 і скільки звучить.</summary>
public sealed record MafiaClipView(string Url, double Seconds);

/// <summary>Репліка ведучого вголос: номер (клієнт грає кожну раз), текст і шматки, що звучать один за одним.</summary>
public sealed record MafiaSpeechView(int Id, string Text, MafiaClipView[] Parts);

/// <summary>
/// Що Глек-ведучий каже вголос (specs/mafia.md «Прохід №3»). Окремо від текстових реплік <see cref="MafiaGlek"/>: там
/// жарти вибирає <c>Ctx.Rng</c>, а голос мусить бути сталим — ті самі рядки озвучуються наперед на старті, і різниця
/// в одну кому дала б інший хеш і кліп, якого в кеші нема.
/// <para>
/// Правило одне: <b>голос не каже нічого, чого не видно всім</b>. Ролей не називає (навіть вигнаного, хоч би стіл і
/// розкривав їх текстом), до мафії окремо не звертається — «прокидається мафія» звучить однаково в усіх, тож хто чув
/// сусідський телефон, нічого нового не дізнався. Ніки — лише в називному після тире: чужі імена ми не відмінюємо.
/// </para>
/// </summary>
public static class MafiaVoiceLines
{
    public const string Intro = "Добрий вечір, село. Роздивіться свої ролі — і нікому ні слова.";
    public const string Night = "Ніч. Село засинає… Прокидається мафія. І всі, кому вночі є діло.";
    public const string QuietNight = "Перша ніч. Село засинає… Прокидається мафія — сьогодні лише знайомиться, без ножів.";
    public const string Morning = "Ранок. Село прокидається.";
    public const string AllAlive = "Ранок. Село прокидається. Уночі всі вціліли.";
    public const string QuietMorning = "Ранок. Село прокидається. Ніч була тиха.";
    public const string Vote = "Досить балачок. Голосуємо: кого виганяємо?";
    public const string NoExile = "Село не дійшло згоди. Ніхто нікуди не йде.";
    public const string CivilWin = "Гру закінчено. Перемогли мирні!";
    public const string MafiaWin = "Гру закінчено. Перемогла мафія!";
    public const string ManiacWin = "Гру закінчено. Переміг маньяк!";
    public const string Draw = "Гру закінчено. Нічия — у селі нема кому судити.";

    public static string Killed(string nick) => $"Уночі не стало — {nick}.";
    public static string KilledTwo(string a, string b) => $"Уночі не стало — {a} і {b}.";
    public static string Exiled(string nick) => $"Село вирішило: за ворота йде — {nick}.";

    /// <summary>Усе, що не залежить від ніків, — на прогрів.</summary>
    public static readonly string[] Fixed =
        [Intro, Night, QuietNight, Morning, AllAlive, QuietMorning, Vote, NoExile, CivilWin, MafiaWin, ManiacWin, Draw];
}

public sealed partial class Mafia
{
    /// <summary>Скільки репліка може запізнитись: пізніше вона вже про іншу фазу, і тиша краща.</summary>
    public const int VoiceGraceMs = 3_000;

    string _voiceName = "ostap";
    IDotepyVoice _voice = DotepyNoVoice.Instance;
    MafiaSpeechView? _speech;
    int _speechId;
    /// <summary>Що Глек хоче сказати, коли доспіють кліпи, і до коли ще чекати (гра не чекає — чекає лише репліка).</summary>
    string[]? _want;
    DateTimeOffset _wantUntil;
    /// <summary>Чим скінчилась попередня фаза — голосом (як <c>_lead</c> для тексту, але без жартів і ролей).</summary>
    string[]? _voiceLead;

    bool VoiceOn => _voice.Enabled;

    /// <summary>Остання репліка вголос — шов для тестів.</summary>
    public MafiaSpeechView? Speech => _speech;

    /// <summary>На старті: голос (якщо не «без голосу» і edge-tts є) і в чергу озвучки все, що Глек може сказати за партію.</summary>
    void StartVoice()
    {
        _speech = null;
        _want = null;
        _voiceLead = null;
        _voice = DotepyNoVoice.Instance;
        if (_voiceName == "none") return;
        try
        {
            if (Ctx.Services.GetService<IDotepyVoice>() is { } v && v.Enabled) _voice = v;
        }
        catch (Exception) { /* голос — чужий код; не вийшло — ведемо текстом */ }
        if (!VoiceOn) return;
        Prepare([MafiaVoiceLines.Intro, MafiaVoiceLines.QuietNight, MafiaVoiceLines.Night], urgent: true);
        var lines = new List<string>(MafiaVoiceLines.Fixed);
        foreach (var s in _seats)
        {
            lines.Add(MafiaVoiceLines.Killed(Name(s)));
            lines.Add(MafiaVoiceLines.Exiled(Name(s)));
        }
        Prepare(lines);
    }

    void Prepare(IEnumerable<string> texts, bool urgent = false)
    {
        try { _voice.Prepare(_voiceName, texts, urgent); }
        catch (Exception) { /* без голосу */ }
    }

    DotepyClip? Clip(string text)
    {
        try { return _voice.Ready(_voiceName, text); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Глек скаже ці шматки один за одним, щойно доспіють, але не пізніше ніж за <see cref="VoiceGraceMs"/>. Не
    /// доспіли всі — звучать ті, що є (краще «Ранок. Село прокидається», ніж нічого). Гра на голос не чекає ніколи.
    /// </summary>
    void Speak(string[] parts, bool final = false)
    {
        _want = null;
        if (!VoiceOn || parts.Length == 0) return;
        // Рідкісне (двоє загиблих за ніч) не озвучене наперед — просимо терміново, раптом устигне.
        var missing = parts.Where(p => Clip(p) is null).ToArray();
        if (missing.Length > 0) Prepare(missing, urgent: true);
        _want = parts;
        // Кінець партії: тиків більше не буде, тож звучить те, що готове вже зараз (переможці озвучені на старті).
        _wantUntil = final ? Ctx.Clock.UtcNow : Ctx.Clock.UtcNow.AddMilliseconds(VoiceGraceMs);
        TrySpeak();
    }

    /// <summary>Кличеться з тика (під замком кімнати): дивиться лише в словник готових кліпів у пам'яті.</summary>
    void TrySpeak()
    {
        if (_want is not { } parts) return;
        var clips = new List<MafiaClipView>(parts.Length);
        var text = new List<string>(parts.Length);
        foreach (var p in parts)
            if (Clip(p) is { } c)
            {
                clips.Add(new MafiaClipView(c.Url, Math.Round(c.Seconds, 2)));
                text.Add(p);
            }
        var late = Ctx.Clock.UtcNow >= _wantUntil;
        if (clips.Count < parts.Length && !late) return;
        _want = null;
        if (clips.Count == 0) return;
        _speech = new MafiaSpeechView(++_speechId, string.Join(' ', text), [.. clips]);
        _dirty = true;
    }

    /// <summary>Що Глек каже вголос на вході у фазу. Тексту <see cref="MafiaGlek"/> не чіпає й <c>Ctx.Rng</c> не бере.</summary>
    void VoiceEnter(MafiaPhase phase)
    {
        var before = _voiceLead;
        _voiceLead = null;
        if (!VoiceOn) return;
        switch (phase)
        {
            case MafiaPhase.Intro: Speak([MafiaVoiceLines.Intro]); break;
            case MafiaPhase.Night:
                var night = QuietNight ? MafiaVoiceLines.QuietNight : MafiaVoiceLines.Night;
                if (before is null) Speak([night]);
                else Speak([.. before, night]);
                break;
            case MafiaPhase.Day: Speak(before ?? Morning()); break;
            case MafiaPhase.Vote: Speak([MafiaVoiceLines.Vote]); break;
        }
    }

    /// <summary>Кінець партії: остання новина (якщо є) і переможець.</summary>
    void VoiceEnd(string end)
    {
        var before = _voiceLead;
        _voiceLead = null;
        if (!VoiceOn) return;
        Speak(before is null ? [end] : [.. before, end], final: true);
    }

    /// <summary>Ранкова новина вголос — рівно те, що й так видно в шапці картки.</summary>
    string[] Morning() => _fallen.Length switch
    {
        >= 2 => [MafiaVoiceLines.Morning, MafiaVoiceLines.KilledTwo(Name(_fallen[0]), Name(_fallen[1]))],
        1 => [MafiaVoiceLines.Morning, MafiaVoiceLines.Killed(Name(_fallen[0]))],
        _ => [_day == 1 && !FirstKill(_seats.Length) ? MafiaVoiceLines.QuietMorning : MafiaVoiceLines.AllAlive],
    };
}
