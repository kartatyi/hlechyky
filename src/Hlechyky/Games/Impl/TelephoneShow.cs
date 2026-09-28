using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Репліка Глека на показі: адреса mp3 і скільки звучить (клієнт грає лише з увімкненим «🔊 Глек»).</summary>
public sealed record TelephoneSay(int Id, string Url, double Seconds);

/// <summary>
/// Звання партії (прохід №3, п. 224): «🎨 Пікассо», «✍ Поет» — місцям, «🌀 Злам сенсу» — ланцюжку
/// (<see cref="Owner"/> — чий він, <see cref="From"/>/<see cref="To"/> — з чого почалось і чим скінчилось).
/// </summary>
public sealed record TelephoneAward(string Icon, string Title, int[] Seats, string Text, int? Owner = null, string? From = null, string? To = null);

/// <summary>
/// Показ-кіно (прохід №3, п. 166) і звання партії (п. 224). Показ гортається сам: фраза стоїть, скільки її читати
/// (або скільки звучить Глек, якщо кліп уже готовий), малюнок у браузері відтворюється штрих за штрихом за
/// <c>drawMs</c> і ще трохи стоїть, а кінець ланцюжка — довша пауза, щоб розсміятись і наставити ❤. «Далі»
/// гортає одразу (тобто лише пришвидшує), «⏸» зупиняє кіно для всіх. Гра на голос не чекає ніколи: кліп, що не
/// доспів за <see cref="VoiceGraceMs"/>, просто не звучить, а під замком кімнати голос дивиться лише в словник у пам'яті.
/// </summary>
public sealed partial class Telephone
{
    /// <summary>Фраза на екрані: основа й скільки додає кожна літера; межі — щоб і «кіт» устигли, і довге не нудило.</summary>
    public const int TextBaseMs = 2_500, TextPerCharMs = 55, TextMinMs = 3_000, TextMaxMs = 6_500;
    /// <summary>Відтворення малюнка: основа, скільки додає кожен штрих, межі.</summary>
    public const int DrawBaseMs = 1_200, DrawPerOpMs = 70, DrawMinMs = 2_000, DrawMaxMs = 6_000;
    /// <summary>Скільки готовий малюнок ще стоїть після відтворення; порожнє полотно — стільки ж і все.</summary>
    public const int LookMs = 2_500;
    /// <summary>Кінець ланцюжка: довше, щоб роздивитись усе й поставити ❤.</summary>
    public const int ChainEndMs = 4_000;
    /// <summary>Після «▶» кіно рушає не одразу — дати дочитати.</summary>
    public const int ResumeMs = 2_500;
    /// <summary>Скільки ще чекати кліп фрази, якщо на момент показу не доспів. Пізніше — тиша (фраза вже пішла).</summary>
    public const int VoiceGraceMs = 1_500;
    /// <summary>Після репліки Глека — вдих перед наступним записом.</summary>
    const int VoiceTailMs = 900;

    bool _auto = true;
    bool _paused;
    DateTimeOffset _autoAt;
    int _autoMs, _drawMs;

    string _voiceName = "ostap";
    IDotepyVoice _voice = DotepyNoVoice.Instance;
    TelephoneSay? _say;
    int _sayId;
    string? _want;
    DateTimeOffset _wantUntil;

    bool VoiceOn => _voice.Enabled;

    /// <summary>Скільки відтворюється малюнок (0 — порожнє полотно).</summary>
    public static int DrawMs(int ops) => ops <= 0 ? 0 : Math.Clamp(DrawBaseMs + DrawPerOpMs * ops, DrawMinMs, DrawMaxMs);

    public static int TextMs(string? text) => Math.Clamp(TextBaseMs + TextPerCharMs * (text?.Length ?? 0), TextMinMs, TextMaxMs);

    /// <summary>Щойно відкрили запис: скільки він стоїть і що каже Глек.</summary>
    void Shown()
    {
        var chain = _chains[_chain];
        var e = chain[_shown - 1];
        _drawMs = e.Kind == "drawing" ? DrawMs(e.Ops?.Length ?? 0) : 0;
        var ms = e.Kind == "drawing" ? _drawMs + LookMs : TextMs(e.Text);
        if (_shown >= chain.Count) ms += ChainEndMs;
        _autoMs = ms;
        _autoAt = Now.AddMilliseconds(ms);
        _say = null;
        _want = null;
        if (VoiceOn && e.Kind == "text" && e.Text is { Length: > 0 } text && text != Shrug)
        {
            _want = text;
            _wantUntil = Now.AddMilliseconds(VoiceGraceMs);
            if (Clip(text) is null) Prepare([text], urgent: true);
            TrySay();
        }
    }

    ActResult Pause(int seat)
    {
        if (_phase != Reveal) return ActResult.Fail("Показ ще не почався");
        if (!_auto) return ActResult.Fail("Тут гортають «Далі»");
        if (!Present(seat)) return ActResult.Fail("Ти вже не за столом");
        _paused = !_paused;
        if (!_paused) _autoAt = Now.AddMilliseconds(Math.Min(_autoMs, ResumeMs));
        _dirty = true;
        return ActResult.Accept(_paused ? "⏸ Кіно на паузі — «Далі» гортає вручну" : "▶ Кіно далі");
    }

    // ---------------------------------------------------------------------------------------
    // Голос Глека — зразок Мафії/Байкарів: гра лише питає словник готових кліпів і ставить у чергу.
    // ---------------------------------------------------------------------------------------

    void StartVoice()
    {
        _say = null;
        _want = null;
        _paused = false;
        _voice = DotepyNoVoice.Instance;
        if (_voiceName == "none") return;
        try
        {
            if (Ctx.Services.GetService<IDotepyVoice>() is { } v && v.Enabled) _voice = v;
        }
        catch (Exception) { /* голос — чужий код; не вийшло — показ іде мовчки */ }
        if (!VoiceOn) return;
        foreach (var chain in _chains)
            if (chain.Count > 0 && chain[0].Text is { } jug) Prepare([jug]);
    }

    /// <summary>Фраза лягла в ланцюжок: озвучуємо заздалегідь, до показу ще кілька кроків.</summary>
    void VoiceAhead(string text)
    {
        if (VoiceOn && text != Shrug) Prepare([text]);
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

    /// <summary>З тика (під замком): кліп фрази доспів — Глек читає, а кіно на цьому записі стоїть, поки він не скаже.</summary>
    void TrySay()
    {
        if (_want is not { } text) return;
        if (Clip(text) is { } clip)
        {
            _want = null;
            _say = new TelephoneSay(++_sayId, clip.Url, Math.Round(clip.Seconds, 2));
            var end = Now.AddMilliseconds(clip.Seconds * 1000 + VoiceTailMs);
            if (end > _autoAt)
            {
                _autoMs += (int)(end - _autoAt).TotalMilliseconds;
                _autoAt = end;
            }
            _dirty = true;
        }
        else if (Now >= _wantUntil) _want = null;
    }

    // ---------------------------------------------------------------------------------------
    // Звання партії
    // ---------------------------------------------------------------------------------------

    /// <summary>Службові слова не рахуються за «спільне» між першою й останньою фразою.</summary>
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "про", "під", "над", "для", "без", "або", "але", "так", "щоб", "він", "вона", "воно", "вони", "його", "там", "тут",
        "цей", "ця", "це", "той", "та", "те", "які", "який", "яка", "між", "при", "біля", "коло", "через", "дуже", "теж",
    };

    /// <summary>Корені слів фрази: лише слова від трьох літер, перші чотири літери (відмінки й «велосипед/велосипеді»).</summary>
    public static HashSet<string> Roots(string? text)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return roots;
        var word = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant() + " ")
        {
            if (char.IsLetter(ch)) { word.Append(ch); continue; }
            if (ch is '\'' or '’' or 'ʼ') continue;   // «м'ята» — одне слово
            var w = word.ToString();
            word.Clear();
            if (w.Length < 3 || Stop.Contains(w)) continue;
            roots.Add(w.Length > 4 ? w[..4] : w);
        }
        return roots;
    }

    TelephoneAward[] Awards()
    {
        var draw = new int[Seats];
        var words = new int[Seats];
        foreach (var chain in _chains)
            foreach (var e in chain)
                if (e.Seat >= 0) (e.Kind == "drawing" ? draw : words)[e.Seat] += e.Likes.Count;
        var list = new List<TelephoneAward>(3);
        if (Best(draw) is { } picasso) list.Add(new("🎨", "Пікассо", picasso.Seats, $"найбільше ❤ за малюнки — {picasso.N}"));
        if (Best(words) is { } poet) list.Add(new("✍", "Поет", poet.Seats, $"найбільше ❤ за слова — {poet.N}"));
        if (Broken() is { } broken) list.Add(broken);
        return [.. list];
    }

    (int[] Seats, int N)? Best(int[] likes)
    {
        var best = 0;
        foreach (var s in _order) if (!_left.Contains(s)) best = Math.Max(best, likes[s]);
        if (best == 0) return null;
        int[] seats = [.. _order.Where(s => !_left.Contains(s) && likes[s] == best)];
        // Звання «всім порівну» нічого не каже — тоді його просто нема.
        return seats.Length > 1 && seats.Length == _order.Count(s => !_left.Contains(s)) ? null : (seats, best);
    }

    /// <summary>
    /// «🌀 Злам сенсу» — ланцюжок, де остання фраза не має з першою жодного спільного слова (корені, без службових).
    /// Таких кілька — найдовший (більше рук доклались до зламу). Ланцюжок без жодного опису не рахується.
    /// </summary>
    TelephoneAward? Broken()
    {
        var pick = -1;
        Entry? from = null, to = null;
        for (var c = 0; c < _chains.Length; c++)
        {
            var chain = _chains[c];
            if (!HasPlayers(chain) || chain.Count < 3 || chain[0].Kind != "text") continue;
            var last = chain.LastOrDefault(e => e.Kind == "text" && e.Seat != Jug && e.Text != Shrug);
            if (last is null || ReferenceEquals(last, chain[0])) continue;
            var first = Roots(chain[0].Text);
            if (first.Count == 0 || first.Overlaps(Roots(last.Text))) continue;
            if (pick >= 0 && _chains[pick].Count >= chain.Count) continue;
            pick = c;
            from = chain[0];
            to = last;
        }
        if (pick < 0 || from is null || to is null) return null;
        return new("🌀", "Злам сенсу", [], $"почалось із «{from.Text}», а скінчилось «{to.Text}»", _order[pick], from.Text, to.Text);
    }
}
