namespace Hlechyky.Games.Impl;

/// <summary>
/// Що каже Дядько Глек у «Байкарях» (голос — той самий, що в Дотепах: <see cref="IDotepyVoice"/> поверх
/// <see cref="TtsService"/>). В одному місці, бо ті самі рядки й озвучують наперед, і кажуть у грі: різниця в одну кому
/// дала б інший хеш і репліку, якої в кеші нема.
/// <para>
/// За рішенням користувача Глек читає <b>питання</b>, <b>вердикти з ніками</b> на розкритті й <b>переможця</b> — карток
/// не читає. Ніки — завжди в називному й після тире чи двокрапки: чужі імена ми не відмінюємо, а рід не знаємо
/// («На гачку — Петро», а не «Петро повірив»).
/// </para>
/// </summary>
public static class BluffLines
{
    /// <summary>Пропуск у питанні голосом — як цензорський писк.</summary>
    public const string Beep = "…біп…";
    public const string FinalPrefix = "Останнє питання, очки вдвічі! ";
    public const string Draw = "Нічия! Брехали всі однаково вправно.";

    /// <summary>«Кров восьминога — біп — кольору.» На останньому — з попередженням про ×2.</summary>
    public static string Question(string q, bool final)
    {
        var text = q.Replace(BluffBank.Blank, " " + Beep + " ");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return (final ? FinalPrefix : "") + End(text);
    }

    /// <summary>Брехня гравця, на яку купились: «На гачку — Петро і Ганна! Автор брехні — Оля.»</summary>
    public static string Lie(string victims, string authors, int authorCount) =>
        $"На гачку — {victims}! {(authorCount > 1 ? "Автори брехні" : "Автор брехні")} — {authors}.";

    /// <summary>Заготовка Глека, на яку купились.</summary>
    public static string Decoy(string victims) => $"На гачку — {victims}! А цю байку склав сам Глек.";

    /// <summary>Правда: «А правда — гасі! З нюхом — Оля і Петро.»</summary>
    public static string Truth(string answer, string finders, int found, int present) =>
        found == 0 ? $"А правда — {answer}! І ніхто не вгадав."
        : found >= present && present >= 2 ? $"А правда — {answer}! Вгадали всі."
        : $"А правда — {answer}! З нюхом — {finders}.";

    public static string Win(string nick) => $"Перемагає {nick}! Браво!";
    public static string Wins(string names) => $"Перемагають {names}! Браво!";

    static string End(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "…";
        return ".!?…".Contains(s[^1]) ? s : s + ".";
    }
}
