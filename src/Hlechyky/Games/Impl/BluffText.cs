using System.Globalization;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Перевірка тексту «Байкарів»: чи брехня часом не правда (<see cref="LooksTrue"/>) і чи дві брехні — одна й та
/// сама (<see cref="LooksSame"/>). Чиста, без стану: на вході рядки, на виході так/ні. Сита різні навмисно:
/// <see cref="LooksTrue"/> грубе (відмінки, одруківки, правда у фразі, шматок правди) — прийнята правда зіпсувала б
/// питання всім, а хибна відмова чесній брехні дешева; <see cref="LooksSame"/> тонке (лише закінчення) — інакше різні
/// брехні «кота» й «кита» злилися б в одну картку з чужим текстом. Синоніми не ловимо: за них відповідає <c>accept</c>.
/// </summary>
public static class BluffText
{
    /// <summary>
    /// Для порівнянь: NFKC (широкі й «математичні» літери — звичайними), нижній регістр, усі апострофи — один «'»,
    /// двійники з інших абеток — однією літерою (<see cref="Fold"/>), невидимі знаки й наголоси геть, знак-двійник
    /// літери посеред слова — тією літерою (<see cref="SymbolTwin"/>: «МА×ОРКА» — це «махорка»), решта не-літер — пробіл,
    /// пробіли злиті; у слові, де є й літери, й цифри, 0 і 3 читаються як О і З (на великій картці їх не відрізнити).
    /// «Гасі!» → «гасі», «Пд. Буг» → «пд буг», «BOДEHЬ» і «В0ДЕНЬ» → «водень».
    /// </summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        // Спершу лише видимі руни: знакові-двійнику треба бачити сусідів. Невидимі (нульовий пробіл, заповнювачі
        // хангиля, теги) і знаки наголосу геть: інакше «во[заповнювач]день» на картці читалась би як «водень», а для
        // перевірки була б двома словами — правда проскочила б непоміченою.
        var runes = new List<Rune>(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            if (Invisible(rune.Value)) continue;
            var cat = Rune.GetUnicodeCategory(rune);
            if (cat is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            runes.Add(rune);
        }
        var sb = new StringBuilder(s.Length);
        var gap = false;
        for (var i = 0; i < runes.Count; i++)
        {
            var rune = runes[i];
            var c = rune.Value;
            var twin = SymbolTwin(c);
            if (twin != '\0' && (LetterAt(runes, i - 1) || LetterAt(runes, i + 1)))
            {
                if (gap && sb.Length > 0) sb.Append(' ');
                gap = false;
                sb.Append(twin);
            }
            else if (Rune.IsLetterOrDigit(rune) || c == '\'' || c == '-' || IsApostrophe(c))
            {
                if (gap && sb.Length > 0) sb.Append(' ');
                gap = false;
                if (rune.IsBmp) sb.Append(Fold((char)c));
                else sb.Append(rune.ToString());
            }
            else gap = true;
        }
        FoldDigitsInWords(sb);
        return sb.ToString();
    }

    /// <summary>Сусід — літера чи теж знак-двійник («∏℮ТРО»: ∏ стоїть біля ℮, а той — біля літери).</summary>
    static bool LetterAt(List<Rune> runes, int i) =>
        i >= 0 && i < runes.Count && (Rune.IsLetter(runes[i]) || SymbolTwin(runes[i].Value) != '\0');

    /// <summary>
    /// Знаки, що на великій картці посеред слова вдають літеру: × — Х, | — І, € і ∈ — Є, ∏ — П, ∆ — Д, ∧ — Л, ⊤ — Т,
    /// ℮ — Е, ¢ — С. Окремо (2×2, 5 €) вони лишаються знаками. <c>'\0'</c> — не двійник.
    /// </summary>
    static char SymbolTwin(int c) => c switch
    {
        '×' => 'х',
        '|' or '¦' or '∣' => 'і',
        '€' or '∈' => 'є',
        '∏' => 'п',
        '∆' => 'д',
        '∧' => 'л',
        '⊤' => 'т',
        '℮' => 'е',
        '¢' => 'с',
        _ => '\0',
    };

    static bool IsApostrophe(int c) => c is '’' or 'ʼ' or '‘' or '`' or '´' or 'ʻ' or '′';

    /// <summary>
    /// Двійники, яких на великій картці (картки — ВЕЛИКИМИ літерами) не відрізнити: латинські й грецькі — до кирилиці
    /// (B/В, H/Н, M/М, T/Т, безкрапкова ı/І, грецькі Е/Н/П…), кілька неукраїнських кириличних — до латиниці (S, J).
    /// Порівнюємо обидва боки однаково, тож латинська правда (HOLLYWOODLAND) так само впізнає кириличного двійника.
    /// Літер інших письмен (черокі, лісу, капітель…) тут нема: брехню з ними <see cref="ForeignLetters"/> не пускає.
    /// </summary>
    static char Fold(char ch) => ch switch
    {
        '’' or 'ʼ' or '‘' or '`' or '´' or 'ʻ' or '′' => '\'',
        // латиниця → кирилиця
        'a' => 'а', 'b' => 'в', 'c' => 'с', 'e' => 'е', 'h' => 'н', 'i' => 'і', 'k' => 'к', 'm' => 'м', 'o' => 'о',
        'p' => 'р', 't' => 'т', 'x' => 'х', 'y' => 'у', 'ï' => 'ї',
        'ı' => 'і', 'ĸ' => 'к', 'þ' => 'р', 'ë' => 'ё', 'è' => 'ѐ',
        // греція → кирилиця (ню й дзета — до латиниці: Ν і Ζ схожі лише на N і Z)
        'α' => 'а', 'β' => 'в', 'γ' => 'г', 'δ' => 'д', 'ε' => 'е', 'η' => 'н', 'ι' => 'і', 'κ' => 'к', 'λ' => 'л',
        'μ' => 'м', 'ο' => 'о', 'π' => 'п', 'ρ' => 'р', 'τ' => 'т', 'υ' => 'у', 'φ' => 'ф', 'χ' => 'х', 'ϊ' => 'ї',
        'ν' => 'n', 'ζ' => 'z',
        // неукраїнська кирилиця, що вдає латиницю
        'ѕ' => 's', 'ј' => 'j', 'ԛ' => 'q', 'ԝ' => 'w', 'ӏ' => 'і',
        _ => ch,
    };

    /// <summary>
    /// Літери, з яких може складатись брехня: латиниця Європи (основна, Latin-1, Extended-A і румунські ș ț), основна
    /// грецька, кирилиця (U+0400–U+045F і Ґ). Двійників усередині цього набору зводить <see cref="Fold"/>.
    /// </summary>
    static bool PlainLetter(int c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
        || c is >= 0x00C0 and <= 0x017F
        || c is >= 0x0218 and <= 0x021B
        || c is >= 0x0386 and <= 0x03CE
        || c is >= 0x0400 and <= 0x045F
        || c is 0x0490 or 0x0491;

    /// <summary>
    /// Чи є в тексті літера чи цифра не з нашого набору (<see cref="PlainLetter"/>, цифри 0–9) — хоч як написано, хоч
    /// після NFKC. Черокі й лісу (цілі абетки двійників M, A, H, T, B, P), капітель, IPA, розширена латиниця й
    /// кирилиця (Ƃ Ƅ Ʒ Ү Һ), «математичні» й широкі літери, чужі цифри (деванагарі нуль): на великій картці половина з
    /// них вдає кирилицю, а всіх двійників усіх письмен не переловиш — тож такі брехні просимо переписати.
    /// Апострофи-«літери» (ʼ) і невидимі знаки не рахуються.
    /// </summary>
    public static bool ForeignLetters(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return Foreign(s) || Foreign(s.Normalize(NormalizationForm.FormKC));

        static bool Foreign(string t)
        {
            foreach (var rune in t.EnumerateRunes())
            {
                var c = rune.Value;
                if (IsApostrophe(c) || Invisible(c)) continue;
                if (Rune.IsLetter(rune) && !PlainLetter(c)) return true;
                if (Rune.IsDigit(rune) && c is not (>= '0' and <= '9')) return true;
            }
            return false;
        }
    }

    /// <summary>У слові, де є і літери, і цифри, 0 → о, 3 → з: «В0ДЕНЬ» на картці — те саме «ВОДЕНЬ».</summary>
    static void FoldDigitsInWords(StringBuilder sb)
    {
        var start = 0;
        for (var i = 0; i <= sb.Length; i++)
        {
            if (i < sb.Length && sb[i] is not (' ' or '-')) continue;
            bool letter = false, digit = false;
            for (var j = start; j < i; j++)
            {
                if (char.IsDigit(sb[j])) digit = true;
                else if (char.IsLetter(sb[j])) letter = true;
            }
            if (letter && digit)
                for (var j = start; j < i; j++)
                    if (sb[j] == '0') sb[j] = 'о';
                    else if (sb[j] == '3') sb[j] = 'з';
            start = i + 1;
        }
    }

    /// <summary>
    /// Знаки, яких на екрані не видно (default-ignorable з Unicode): м'який перенос, нульові пробіли й з'єднувачі,
    /// керування напрямком, заповнювачі хангиля (вони ще й «літери»), варіанти гліфів, теги.
    /// </summary>
    static bool Invisible(int c) =>
        c is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFEFF or 0xFFA0
        || c is >= 0x180B and <= 0x180F
        || c is >= 0x200B and <= 0x200F
        || c is >= 0x202A and <= 0x202E
        || c is >= 0x2060 and <= 0x206F
        || c is >= 0xFE00 and <= 0xFE0F
        || c is >= 0x1BCA0 and <= 0x1BCA3
        || c is >= 0x1D173 and <= 0x1D17A
        || c is >= 0xE0000 and <= 0xE0FFF;

    /// <summary>Порожня клітинка Брайля: не пробіл для .NET, а на екрані — пробіл.</summary>
    static bool Blank(int c) => c == 0x2800;

    /// <summary>
    /// Службові слова одного значення зводимо до одного вигляду (у/в, з/із/зі/зо, і/й): «із сліз» при правді «зі сліз»
    /// — та сама правда, а не брехня.
    /// </summary>
    static string Canon(string t) => t switch
    {
        "у" => "в",
        "із" or "зі" or "зо" => "з",
        "й" => "і",
        _ => t,
    };

    /// <summary>Слова: <see cref="Norm"/>, поділене пробілами й дефісами, без порожніх і без апострофів по краях.</summary>
    public static List<string> Tokens(string? s)
    {
        var list = new List<string>();
        foreach (var part in Norm(s).Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim('\'');
            if (t.Length > 0) list.Add(Canon(t));
        }
        return list;
    }

    /// <summary>
    /// Чи є слово, у якому змішано абетки (латиниця, кирилиця, греція): «кисeнь» із латинською e на картці не
    /// відрізнити від чесного, тож таку брехню просимо переписати однією абеткою. Різні абетки в різних словах —
    /// можна («iPhone-ом»).
    /// </summary>
    public static bool MixedScripts(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Normalize(NormalizationForm.FormKC);
        var scripts = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            var c = rune.Value;
            if (Invisible(c) || IsApostrophe(c) || c == '\''
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format) continue;
            if (!Rune.IsLetter(rune))
            {
                scripts = 0;           // межа слова
                continue;
            }
            scripts |= Script(c);
            if (scripts is not (0 or 1 or 2 or 4)) return true;
        }
        return false;
    }

    /// <summary>
    /// 1 — латиниця, 2 — кирилиця, 4 — грецька, 0 — інше (його сюди не пускає ще <see cref="ForeignLetters"/>).
    /// </summary>
    static int Script(int c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= 0x00C0 and <= 0x024F) or (>= 0x1E00 and <= 0x1EFF) ? 1
        : c is (>= 0x0400 and <= 0x052F) or (>= 0x1C80 and <= 0x1C8F) or (>= 0x2DE0 and <= 0x2DFF) or (>= 0xA640 and <= 0xA69F) ? 2
        : c is (>= 0x0370 and <= 0x03FF) or (>= 0x1F00 and <= 0x1FFF) ? 4
        : 0;

    /// <summary>Відстань Левенштейна (вставка, видалення, заміна — по одиниці; без перестановок).</summary>
    public static int Lev(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    static bool HasDigit(string s)
    {
        foreach (var ch in s) if (char.IsDigit(ch)) return true;
        return false;
    }

    /// <summary>
    /// Одне слово правди — те саме? Грубе сито для <see cref="LooksTrue"/>: точно; одруківка (від чотирьох літер, одна
    /// правка); відмінок (спільний корінь із трьох літер, дві правки); довгий відмінок (спільний початок майже на все
    /// коротше слово, а різниця — не довша за закінчення: «кроликів»/«кролики», але не «hollywood»/«hollywoodhills»).
    /// Числа — лише точно: 1855 при правді 1854 — чесна брехня, а не одруківка.
    /// </summary>
    public static bool TokenMatch(string a, string b)
    {
        if (a == b) return true;
        if (HasDigit(a) || HasDigit(b)) return false;
        var min = Math.Min(a.Length, b.Length);
        if (min < 4) return false;
        var lev = Lev(a, b);
        if (lev <= 1) return true;
        var pre = CommonPrefix(a, b);
        if (pre >= 3 && lev <= 2) return true;
        return min >= 5 && pre >= Math.Max(4, min - 2) && Math.Abs(a.Length - b.Length) <= 3;
    }

    /// <summary>
    /// Закінчення іменників і прикметників (і кілька дієслівних): ними й лише ними можуть відрізнятись два написання
    /// однієї брехні — «салі»/«салом», «Марсі»/«Марсу», «Ірак»/«Іраку». Дієслівних «-ла/-ли/-ти» тут нема навмисно: вони
    /// відрізали б від «школа» не те, що від «школу».
    /// </summary>
    static readonly HashSet<string> Endings = new(StringComparer.Ordinal)
    {
        "а", "я", "у", "ю", "і", "ї", "и", "е", "є", "о", "ь", "й",
        "ом", "ем", "єм", "ою", "ею", "єю", "ам", "ям", "ах", "ях", "ів", "їв", "ей", "ій", "ої", "ий", "им", "ім",
        "их", "іх", "ую", "юю", "ов", "ами", "ями", "ого", "ому", "ові", "еві", "єві", "ього", "ьому",
        "ють", "ять", "уть", "ать", "ить",
    };

    /// <summary>
    /// Основа слова: без найдовшого закінчення з <see cref="Endings"/>, але щонайменше три літери. «гасом» → «гас»,
    /// «Іраку» → «ірак», «кита» → «кит», «Китаї» → «кита». Числа — як є.
    /// </summary>
    static string Stem(string w)
    {
        if (HasDigit(w)) return w;
        for (var n = Math.Min(4, w.Length - 3); n >= 1; n--)
            if (Endings.Contains(w[^n..])) return w[..^n];
        return w;
    }

    /// <summary>
    /// Одне слово брехні — те саме? Тонке сито для <see cref="LooksSame"/>: точно або та сама основа з іншим
    /// закінченням. Одна літера посеред чи наприкінці основи — уже інше слово: «кота»/«кита», «Іраку»/«Ірану»,
    /// «Австрії»/«Австралії», «Ірландії»/«Ісландії», «кита»/«Китаї» — різні брехні й різні картки.
    /// </summary>
    public static bool SameWord(string a, string b) => a == b || (!HasDigit(a) && !HasDigit(b) && Stem(a) == Stem(b));

    /// <summary>
    /// Кожне слово <paramref name="need"/> має свою пару в <paramref name="have"/> (кожне слово <paramref name="have"/>
    /// — щонайбільше для одного). Жадібно за порядком: для коротких фраз цього досить.
    /// </summary>
    public static bool AllIn(IReadOnlyList<string> need, IReadOnlyList<string> have)
    {
        if (need.Count == 0 || need.Count > have.Count) return false;
        var used = new bool[have.Count];
        foreach (var n in need)
        {
            var found = false;
            for (var j = 0; j < have.Count; j++)
            {
                if (used[j] || !TokenMatch(n, have[j])) continue;
                used[j] = found = true;
                break;
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>Скільки літер і цифр у рядку (після <see cref="Norm"/>).</summary>
    public static int Letters(string? s)
    {
        var n = 0;
        foreach (var ch in Norm(s)) if (char.IsLetterOrDigit(ch)) n++;
        return n;
    }

    /// <summary>Чи ця «брехня» насправді правда цього питання (відповідь або будь-яка форма з <c>accept</c>).</summary>
    public static bool LooksTrue(string? lie, BluffQuestion question) => LooksTrue(lie, question.Forms);

    /// <summary>
    /// Правда — і як написано, і як прочитає око: цифра окремим словом на великій картці — та сама літера («0 четвертій»
    /// = «о четвертій», «3 друзями» = «з друзями»), тож коли такі є в брехні чи в правді, звіряємо ще й це прочитання.
    /// Лише для правди: у злитті брехень (<see cref="LooksSame"/>) «3 коти» й «коти» — різні картки.
    /// </summary>
    public static bool LooksTrue(string? lie, IEnumerable<string> forms)
    {
        var list = forms as IReadOnlyList<string> ?? [.. forms];
        if (LooksTrueAsWritten(lie, list)) return true;
        var read = ReadLoneDigits(Norm(lie));
        var changed = read is not null;
        var alt = new List<string>(list.Count);
        foreach (var f in list)
        {
            var n = Norm(f);
            if (ReadLoneDigits(n) is { } r)
            {
                alt.Add(r);
                changed = true;
            }
            else alt.Add(n);
        }
        return changed && LooksTrueAsWritten(read ?? Norm(lie), alt);
    }

    /// <summary>
    /// Цифри 0 і 3, що стоять окремим словом (уже після <see cref="Norm"/>), — літерами О і З; <c>null</c>, коли таких нема.
    /// </summary>
    static string? ReadLoneDigits(string norm)
    {
        char[]? buf = null;
        for (var i = 0; i < norm.Length; i++)
        {
            if (norm[i] is not ('0' or '3')) continue;
            if (i > 0 && norm[i - 1] is not (' ' or '-')) continue;
            if (i + 1 < norm.Length && norm[i + 1] is not (' ' or '-')) continue;
            buf ??= norm.ToCharArray();
            buf[i] = norm[i] == '0' ? 'о' : 'з';
        }
        return buf is null ? null : new string(buf);
    }

    static bool LooksTrueAsWritten(string? lie, IReadOnlyList<string> forms)
    {
        var norm = Norm(lie);
        var tokens = Tokens(lie);
        var letters = Letters(lie);
        foreach (var f in forms)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (norm.Length > 0 && norm == Norm(f)) return true;
            var ft = Tokens(f);
            if (AllIn(ft, tokens)) return true;                     // правда всередині фрази: «звісно ж, на гасі»
            // Шматок правди: «Буг» для «Південний Буг». Шматок має бути щонайменше половиною змістовних слів форми:
            // інакше «фрукт» ставав би правдою через форму банку «овоч, а не фрукт».
            if (letters >= 3 && AllIn(tokens, ft) && tokens.Count * 2 >= Meaningful(ft).Count) return true;
            // Те, що злилося б із правдою в одну картку (інше службове слово, закінчення), — теж правда.
            if (norm.Length > 0 && LooksSame(lie, f)) return true;
        }
        return false;
    }

    /// <summary>
    /// Службові слова, що не міняють брехню: «у хвості» і «хвості» — одна картка (у питанні й так стоїть «в ___»).
    /// Лише ті, що не несуть напрямку: «до сонця» і «від сонця», «під столом» і «над столом» — різні брехні.
    /// «у», «із», «зі», «зо», «й» сюди доходять уже як «в», «з», «і» (<see cref="Canon"/>).
    /// </summary>
    static readonly HashSet<string> Small = new(StringComparer.Ordinal)
    {
        "в", "на", "з", "і", "та", "а", "ж", "же",
    };

    /// <summary>Слова без службових; якщо нічого не лишилось — як були.</summary>
    static List<string> Meaningful(List<string> tokens)
    {
        var list = tokens.FindAll(t => !Small.Contains(t));
        return list.Count > 0 ? list : tokens;
    }

    /// <summary>
    /// Дві брехні — одна картка? Однакові після <see cref="Norm"/> або слово в слово з іншими закінченнями
    /// (<see cref="SameWord"/>), у тому самому порядку, без службових слів («у хвості» = «хвості»). Брехня з самих
    /// смайликів нормалізується в порожнечу — такі порівнюємо як написано.
    /// </summary>
    public static bool LooksSame(string? a, string? b)
    {
        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0)
            return string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
        if (na == nb) return true;
        var ta = Meaningful(Tokens(a));
        var tb = Meaningful(Tokens(b));
        if (ta.Count != tb.Count || ta.Count == 0) return false;
        for (var i = 0; i < ta.Count; i++)
            if (!SameWord(ta[i], tb[i])) return false;
        return true;
    }

    /// <summary>
    /// Текст для картки: керівні знаки й переноси — пробілом, пробіли (зокрема нерозривні й порожня клітинка Брайля)
    /// злиті, краї обрізані, невидимі знаки прибрані (крім ZWJ і варіантів гліфа — на них тримаються смайлики).
    /// Регістр і пунктуацію автора не чіпаємо.
    /// </summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(s.Length);
        var gap = false;
        foreach (var rune in s.EnumerateRunes())
        {
            var c = rune.Value;
            var cat = Rune.GetUnicodeCategory(rune);
            var keep = c == 0x200D || c is >= 0xFE00 and <= 0xFE0F;
            if (!keep && (Invisible(c) || cat == UnicodeCategory.Format)) continue;
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) || Blank(c)
                || cat is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                gap = true;
                continue;
            }
            if (gap && sb.Length > 0) sb.Append(' ');
            gap = false;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}
