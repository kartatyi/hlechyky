namespace Hlechyky;

/// <summary>
/// Відмінки ніка для людських речень: «дарує Петрові», «У Петра це вже є», «Цю пісню Оля присвячує Миколі». Ніки тут —
/// здебільшого імена, тож і правила іменні: -а/-я (Оля, Микола, Ольга → Ользі), -о (Петро), -й (Андрій), -ь (Василь),
/// приголосний (Влад, владік), мішана група після ж/ч/ш/щ (Лукаш → Лукашеві). Чого не впізнали — латиниця, цифри,
/// смайли, незмінні закінчення (-і, -у, -е…), іменник середнього роду, — лишається як є: «дарує smaug» читається краще
/// за вигадане «smaugові». Відмінюється лише перше слово (так пишуть і самі люди: «миколі ( справжній )»), а гість —
/// обидва: «гостю Васі».
/// </summary>
public static class NickCases
{
    /// <summary>Кому? — «Петрові», «Олі», «Миколі», «гостю Васі».</summary>
    public static string Dative(string? nick) => Decline(nick, dative: true);

    /// <summary>Кого? — «Петра», «Олі», «Миколи», «гостя Васі».</summary>
    public static string Genitive(string? nick) => Decline(nick, dative: false);

    /// <summary>
    /// Прийменник на початку речення перед словом <paramref name="next"/>: перед голосним — «В» («В Олі»),
    /// інакше — «У» («У Петра»), як велить милозвучність.
    /// </summary>
    public static string AtStart(string next) => next.Length > 0 && Vowels.Contains(char.ToLowerInvariant(next[0])) ? "В" : "У";

    const string Vowels = "аеєиіїоуюяыэё";
    const string Sibilants = "жчшщ";
    const string Apostrophes = "'’ʼ";

    /// <summary>
    /// Імена, яких загальні правила не беруть: м'яке «Ігор», чергування голосних («Федір» → «Федора», «Кіт» → «Кота»),
    /// жіноче «Любов» третьої відміни і сам «гість», з якого починається кожен гостьовий нік.
    /// </summary>
    static readonly Dictionary<string, (string Dat, string Gen)> Special = new(StringComparer.Ordinal)
    {
        ["ігор"] = ("ігореві", "ігоря"),
        ["федір"] = ("федорові", "федора"),
        ["сидір"] = ("сидорові", "сидора"),
        ["кіт"] = ("котові", "кота"),
        ["любов"] = ("любові", "любові"),
        ["гість"] = ("гостю", "гостя"),
    };

    static string Decline(string? nick, bool dative)
    {
        var s = (nick ?? "").Trim();
        if (s.Length == 0) return s;
        var space = s.IndexOf(' ');
        if (space < 0) return Word(s, dative);
        var (first, rest) = (s[..space], s[(space + 1)..]);
        // «гість Вася»: і «гість», і ім'я — «гостю Васі», а не «гостю Вася»
        if (first.Equals(Auth.Guest, StringComparison.OrdinalIgnoreCase)) return Word(first, dative) + " " + Decline(rest, dative);
        return Word(first, dative) + " " + rest;
    }

    static string Word(string word, bool dative)
    {
        if (word.Length < 2 || !word.All(ch => IsCyrillic(ch) || Apostrophes.Contains(ch))) return word;
        var w = word.ToLowerInvariant();
        if (Special.TryGetValue(w, out var forms)) return Like(word, dative ? forms.Dat : forms.Gen);
        var last = w[^1];
        var prev = w[^2];
        string result;
        switch (last)
        {
            // Микола → Миколі / Миколи, Ольга → Ользі, Галка → Галці; після шиплячих — -і в обох: Саша → Саші
            case 'а' when w.Length >= 3 && !Vowels.Contains(prev):
                result = Sibilants.Contains(prev) ? Cut(word, 1) + "і"
                    : !dative ? Cut(word, 1) + "и"
                    : prev switch
                    {
                        'г' => Cut(word, 2) + "зі",
                        'к' => Cut(word, 2) + "ці",
                        'х' => Cut(word, 2) + "сі",
                        _ => Cut(word, 1) + "і",
                    };
                break;
            // Оля → Олі, Ілля → Іллі; після голосного — -ї: Марія → Марії
            case 'я' when w.Length >= 3:
                result = Cut(word, 1) + (Vowels.Contains(prev) ? "ї" : "і");
                break;
            // Петро → Петрові / Петра; «Лео» після голосного не відмінюється
            case 'о' when w.Length >= 3 && !Vowels.Contains(prev):
                result = Cut(word, 1) + (dative ? "ові" : "а");
                break;
            // Андрій → Андрієві / Андрія; прикметник на -ий — як прикметник: Злий → Злому / Злого
            case 'й' when w.Length >= 3:
                result = prev == 'и' ? Cut(word, 2) + (dative ? "ому" : "ого") : Cut(word, 1) + (dative ? "єві" : "я");
                break;
            // Василь → Василеві / Василя
            case 'ь' when w.Length >= 3:
                result = Cut(word, 1) + (dative ? "еві" : "я");
                break;
            default:
                // -і, -у, -е, -и… і апостроф у кінці — як є; решта — приголосний: Влад → Владові, Лукаш → Лукашеві
                if (Vowels.Contains(last) || !char.IsLetter(last) || last is 'а' or 'я' or 'о' or 'й' or 'ь') return word;
                result = word + (dative ? Sibilants.Contains(last) ? "еві" : "ові" : "а");
                break;
        }
        return Loud(word) ? result.ToUpperInvariant() : result;
    }

    static bool IsCyrillic(char ch) => ch is >= (char)0x0400 and <= (char)0x04FF && char.IsLetter(ch);

    static string Cut(string word, int n) => word[..^n];

    /// <summary>Нік великими літерами («ОЛЯ») — і відмінок такий самий («ОЛІ»).</summary>
    static bool Loud(string word) => word.Length > 1 && word == word.ToUpperInvariant() && word != word.ToLowerInvariant();

    /// <summary>Готова форма з таблиці — з тим самим регістром, що в ніку: «ігор» → «ігореві», «Ігор» → «Ігореві».</summary>
    static string Like(string word, string form) =>
        Loud(word) ? form.ToUpperInvariant() : char.IsUpper(word[0]) ? char.ToUpperInvariant(form[0]) + form[1..] : form;
}
