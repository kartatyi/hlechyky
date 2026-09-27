using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Готова репліка Дядька Глека: адреса mp3 і скільки звучить.</summary>
public sealed record DotepyClip(string Url, double Seconds);

/// <summary>
/// Голос ведучого «Дотепів» (specs/dotepy.md §6). Гра лише питає <see cref="Ready"/> і ставить у чергу
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
/// </summary>
public sealed class DotepyVoice(TtsService tts) : IDotepyVoice
{
    public bool Enabled => tts.Enabled;

    public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false) =>
        tts.Enqueue(voice, texts.Select(Clean), urgent);

    public DotepyClip? Ready(string voice, string text) =>
        tts.TryGet(voice, Clean(text)) is { } clip ? new DotepyClip(SvoyaVoice.UrlPrefix + clip.Hash + ".mp3", clip.Seconds) : null;

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
    public const string Round1 = "Раунд перший. Пишіть дотепи!";
    public const string Round2 = "Раунд другий. Кожен голос — подвійний!";
    public const string FinalThree = "Останній дотеп! Одне завдання — на всіх, у кожного три голоси.";
    public const string FinalTwo = "Останній дотеп! Одне завдання — на всіх, у кожного два голоси.";
    public const string Tie = "Порівну. Публіка розділилась.";
    public const string Silence = "Ніхто не проголосував. Буває.";
    public const string StockWin = "Публіка обрала мовчання. Очок за це не дають.";
    public const string GameTie = "Нагорі нічия. Дотепні всі!";
    public const string Gone = "Замало гравців — партію не дограли. Приходьте ще!";

    public static string Win(string nick) => $"Картку забирає {nick}.";
    public static string Sweep(string nick) => $"Розгром! Усі голоси — {nick}!";
    public static string FinalWin(string nick) => $"Останній дотеп забирає {nick}!";
    public static string GameWin(string nick) => $"Перемагає {nick}!";

    /// <summary>Вступ раунду: перший, другий чи фінал (на трьох у фіналі — два голоси, не три).</summary>
    public static string Intro(int round, bool final, int perVoter) =>
        final ? (perVoter >= 3 ? FinalThree : FinalTwo) : round == 1 ? Round1 : Round2;

    /// <summary>Усе, що Глек каже без підстановок, — це можна озвучити ще до першої партії.</summary>
    public static IEnumerable<string> Pure() => [Round1, Round2, FinalThree, FinalTwo, Tie, Silence, StockWin, GameTie, Gone];

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
