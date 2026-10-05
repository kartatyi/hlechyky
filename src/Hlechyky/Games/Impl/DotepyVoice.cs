using System.Collections.Concurrent;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Готова репліка Дядька Глека: адреса mp3 і скільки звучить.</summary>
public sealed record DotepyClip(string Url, double Seconds);

/// <summary>
/// Голос ведучого «Додепів» (specs/dotepy.md §6). Гра лише питає <see cref="Ready"/> і ставить у чергу
/// <see cref="Prepare"/> — обидва не чекають нічого, крім словника в пам'яті: озвучка йде фоном, поза замком кімнати.
/// </summary>
public interface IDotepyVoice
{
    /// <summary>Чи є голос узагалі (edge-tts стоїть і ввімкнений). Ні — гра йде текстом без жодних очікувань.</summary>
    bool Enabled { get; }
    /// <summary>Поставити репліки в чергу. Не блокує. <paramref name="urgent"/> — на самий початок черги.</summary>
    void Prepare(string voice, IEnumerable<string> texts, bool urgent = false);
    /// <summary>Готова репліка або null (ще готується чи не вийшла).</summary>
    DotepyClip? Ready(string voice, string text);
}

/// <summary>
/// Бойовий голос поверх <see cref="TtsService"/>: той самий кеш <c>cache/tts</c> і той самий уже змаплений
/// ендпоінт «Своєї гри» <c>/api/games/svoya/tts/&lt;хеш&gt;.mp3</c> — новий дублював би десять рядків заради іншого
/// шляху (specs/dotepy.md §12.7). Текст для edge-tts чиститься <see cref="Clean"/>: емодзі голосом не прочитаєш.
/// <para>
/// <see cref="Ready"/> кличуть з-під замка кімнати щотика, поки картка чекає на Глека, тож він дивиться <b>лише</b> в
/// словник у пам'яті. <see cref="TtsService.TryGet"/>, що при промаху лізе на диск (<c>.sec</c>/<c>.mp3</c> з
/// минулого запуску), кличе тільки фоновий опитувач <see cref="Poll"/> — по репліках, які гра попросила і яких ще нема.
/// </para>
/// </summary>
public sealed class DotepyVoice(TtsService tts) : IDotepyVoice, IDisposable
{
    /// <summary>Як часто опитувач питає TtsService про репліки, яких чекає гра.</summary>
    public const int PollMs = 200;
    /// <summary>Перші стільки мс репліку питаємо щоразу, далі — раз на <see cref="SlowPollMs"/> (невдала озвучка не «доспіє»).</summary>
    const int FastMs = 30_000, SlowPollMs = 2_000;
    /// <summary>Скільки ще шукаємо репліку, яку попросили: вердикти з ніками готуються на старті й потрібні аж у кінці партії.</summary>
    const int WantMs = 30 * 60_000;
    /// <summary>Стеля словника готових: переросла — чистимо (його наповнить наступне опитування).</summary>
    const int MaxClips = 4096;

    readonly ConcurrentDictionary<string, DotepyClip> _clips = new(StringComparer.Ordinal);
    readonly Dictionary<string, Want> _wanted = new(StringComparer.Ordinal);
    readonly object _lock = new();
    readonly object _pollLock = new();
    Timer? _timer;
    bool _armed;

    sealed class Want(string voice, string text, long since)
    {
        public string Voice { get; } = voice;
        public string Text { get; } = text;
        public long Since { get; } = since;
        public long Next;
    }

    public bool Enabled => tts.Enabled;

    public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false)
    {
        var clean = texts.Select(Clean).ToList();
        tts.Enqueue(voice, clean, urgent);
        foreach (var t in clean) Ask(voice, t);
    }

    public DotepyClip? Ready(string voice, string text)
    {
        var clean = Clean(text);
        if (_clips.TryGetValue(Key(voice, clean), out var clip)) return clip;
        Ask(voice, clean);
        return null;
    }

    static string Key(string voice, string text) => voice + "\n" + text;

    /// <summary>Гра чекає на цю репліку — опитувач перевірить її найближчим проходом.</summary>
    void Ask(string voice, string text)
    {
        var key = Key(voice, text);
        if (_clips.ContainsKey(key)) return;
        lock (_lock)
        {
            if (!_wanted.ContainsKey(key)) _wanted[key] = new Want(voice, text, Environment.TickCount64);
            if (_armed) return;
            _armed = true;
            _timer ??= new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(PollMs, PollMs);
        }
    }

    /// <summary>
    /// Один прохід опитувача (кличе таймер; публічний — щоб тести крутили його без очікувань): кожну репліку, на яку
    /// чекає гра, питаємо в <see cref="TtsService.TryGet"/>; готова — у словник, де її миттєво знайде <see cref="Ready"/>.
    /// Чекати нема на що — таймер засинає до наступного <see cref="Ask"/>.
    /// </summary>
    public void Poll()
    {
        lock (_pollLock)
        {
            List<(string Key, Want W)> due;
            var now = Environment.TickCount64;
            lock (_lock)
            {
                due = new List<(string, Want)>(_wanted.Count);
                foreach (var (k, w) in _wanted)
                {
                    if (now - w.Since > WantMs) continue;
                    if (w.Next <= now) due.Add((k, w));
                }
                foreach (var k in _wanted.Where(p => now - p.Value.Since > WantMs).Select(p => p.Key).ToList()) _wanted.Remove(k);
            }
            foreach (var (key, w) in due)
            {
                TtsClip? clip = null;
                try { clip = tts.TryGet(w.Voice, w.Text); }
                catch (Exception) { /* диск чи кеш спіткнулись — спробуємо наступного разу */ }
                if (clip is not null)
                {
                    if (_clips.Count >= MaxClips) _clips.Clear();
                    _clips[key] = new DotepyClip(SvoyaVoice.UrlPrefix + clip.Hash + ".mp3", clip.Seconds);
                    lock (_lock) _wanted.Remove(key);
                }
                else w.Next = now + (now - w.Since < FastMs ? 0 : SlowPollMs);
            }
            lock (_lock)
            {
                if (_wanted.Count > 0 || _timer is null) return;
                _armed = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _armed = true;      // більше не заводимо
        }
    }

    /// <summary>Розділові знаки, що лишаються для голосу: паузи й інтонацію edge-tts бере саме з них.</summary>
    const string Keep = ".,!?…'’ʼ-–—:;()«»";

    /// <summary>
    /// Текст для голосу: лишаються літери, цифри, пробіли й <see cref="Keep"/> (з тире — у Глековому «Усі голоси —
    /// Оля!» воно пауза), решта (емодзі, решітки, «@») прибирається, пробіли стискаються. Порожнє — «без слів».
    /// На екрані гравці бачать текст як набрано.
    /// </summary>
    public static string Clean(string? text)
    {
        var sb = new StringBuilder((text ?? "").Length);
        var space = false;
        foreach (var ch in text ?? "")
        {
            if (char.IsLetterOrDigit(ch) || Keep.Contains(ch))
            {
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(ch);
                continue;
            }
            switch (char.GetUnicodeCategory(ch))
            {
                // наголос чи бреве над літерою — частина літери, лишаємо; самотній (після емодзі) — геть
                case System.Globalization.UnicodeCategory.NonSpacingMark:
                    if (!space && sb.Length > 0 && char.IsLetter(sb[^1])) sb.Append(ch);
                    break;
                // ZWJ, селектори варіантів емодзі — без сліду
                case System.Globalization.UnicodeCategory.Format:
                case System.Globalization.UnicodeCategory.EnclosingMark:
                    break;
                // пробіл чи викинутий символ (емодзі теж) — на його місці пробіл, щоб «так🔥так» не злиплось у «тактак»
                default:
                    space = true;
                    break;
            }
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 || !s.Any(char.IsLetterOrDigit) ? "без слів" : s;
    }
}

/// <summary>Без голосу: браузер читає сам (speechSynthesis) або мовчить, час — за оцінкою.</summary>
public sealed class DotepyNoVoice : IDotepyVoice
{
    public static readonly DotepyNoVoice Instance = new();
    public bool Enabled => false;
    public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false) { }
    public DotepyClip? Ready(string voice, string text) => null;
}

/// <summary>
/// Що каже Дядько Глек (specs/dotepy.md §6). В одному місці, бо ті самі рядки й кажуть у грі, й озвучують
/// наперед: різниця в одну кому дала б інший хеш і репліку, якої в кеші нема. Нік — завжди в називному,
/// підметом або одразу після тире: чужі імена ми відмінювати не вміємо («Картку забирає Петро», а не «Петрові»).
/// </summary>
public static class DotepyLines
{
    public const string Round1 = "Раунд перший. Пишіть додепи!";
    public const string Round2 = "Раунд другий. Кожен голос — подвійний!";
    public const string FinalThree = "Останній додеп! Одне завдання — на всіх, у кожного три голоси.";
    public const string FinalTwo = "Останній додеп! Одне завдання — на всіх, у кожного два голоси.";
    public const string Tie = "Порівну. Публіка розділилась.";
    public const string Silence = "Ніхто не проголосував. Буває.";
    public const string StockWin = "Публіка обрала мовчання. Очок за це не дають.";
    public const string GameTie = "Нагорі нічия. Додепні всі!";
    /// <summary>Дограли, а очок ні в кого (усі мовчали) — «Додепні всі!» тут звучало б як знущання.</summary>
    public const string GameNone = "Нуль очок на всіх. Глек чекає реваншу!";
    public const string Gone = "Замало гравців — партію не дограли. Приходьте ще!";

    public static string Win(string nick) => $"Картку забирає {nick}.";
    public static string Sweep(string nick) => $"Розгром! Усі голоси — {nick}!";
    public static string FinalWin(string nick) => $"Останній додеп забирає {nick}!";
    public static string GameWin(string nick) => $"Перемагає {nick}!";

    /// <summary>Вступ раунду: перший, другий чи фінал (на трьох у фіналі — два голоси, не три).</summary>
    public static string Intro(int round, bool final, int perVoter) =>
        final ? (perVoter >= 3 ? FinalThree : FinalTwo) : round == 1 ? Round1 : Round2;

    /// <summary>Усе, що Глек каже без підстановок, — це можна озвучити ще до першої партії.</summary>
    public static IEnumerable<string> Pure() => [Round1, Round2, FinalThree, FinalTwo, Tie, Silence, StockWin, GameTie, GameNone, Gone];

    /// <summary>
    /// «Думки сходяться!» — обидва автори дуелі написали одне й те саме. Одним кліпом, як і звичайна картка:
    /// «{завдання}. Думки сходяться! Обидва написали: {відповідь}»
    /// </summary>
    public static string Jinx(string prompt, string answer) => $"{End(prompt)} Думки сходяться! Обидва написали: {End(answer)}";

    /// <summary>Вердикти з ніком — готуються на старті партії для кожного ніка за столом.</summary>
    public static IEnumerable<string> Named(string nick) => [Win(nick), Sweep(nick), FinalWin(nick), GameWin(nick)];

    static readonly string[] Ordinals = ["Перша", "Друга", "Третя", "Четверта", "П'ята", "Шоста", "Сьома", "Восьма", "Дев'ята"];

    /// <summary>Порядкове для відповіді номер <paramref name="i"/> (з нуля): «Перша», «Друга»… до восьмої й далі числом.</summary>
    public static string Ordinal(int i) => i >= 0 && i < Ordinals.Length ? Ordinals[i] : $"Номер {i + 1}";

    /// <summary>
    /// Читання картки одним кліпом: «{завдання}. Перша: {а}. Друга: {б}.» Крапку не дописуємо, якщо рядок уже
    /// закінчується розділовим знаком: «…то…. Перша» ріже і око, і вухо.
    /// </summary>
    public static string Card(string prompt, IReadOnlyList<string> answers)
    {
        var sb = new StringBuilder();
        sb.Append(End(prompt));
        for (var i = 0; i < answers.Count; i++)
        {
            sb.Append(' ').Append(Ordinal(i)).Append(": ").Append(End(answers[i]));
        }
        return sb.ToString();
    }

    static string End(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "…";
        return ".!?…".Contains(s[^1]) ? s : s + ".";
    }
}
