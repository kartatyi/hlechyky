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
    /// пробіли злиті; у слові, де є літери, 0 і 3 читаються як О і З (на великій картці їх не відрізнити,
    /// <see cref="FoldDigitsInWords"/>).
    /// «Гасі!» → «гасі», «Пд. Буг» → «пд буг», «BOДEHЬ» і «В0ДЕНЬ» → «водень».
    /// </summary>
    public static string Norm(string? s) => Norm(s, readDigits: true);

    /// <summary>
    /// <see cref="Norm(string?)"/>; з <paramref name="readDigits"/> = <c>false</c> — без читання 0 і 3 як літер. Це
    /// прочитання потрібне склеєній правді з числом: «35разів» — те саме «35 разів», а не «з5разів».
    /// </summary>
    static string Norm(string? s, bool readDigits)
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
        if (readDigits) FoldDigitsInWords(sb);
        return sb.ToString();
    }

    /// <summary>Сусід — літера чи теж знак-двійник («∏℮ТРО»: ∏ стоїть біля ℮, а той — біля літери).</summary>
    static bool LetterAt(List<Rune> runes, int i) =>
        i >= 0 && i < runes.Count && (Rune.IsLetter(runes[i]) || SymbolTwin(runes[i].Value) != '\0');

    /// <summary>
    /// Знаки, що на великій картці біля літери вдають літеру: × ✕ ╳ — Х, | │ — І, ◯ ○ ° ∅ — О, € і ∈ — Є, ∏ — П,
    /// ∆ — Д, ∧ — Л, ⊤ — Т, ℮ — Е, ¢ — С. Окремо (2×2, 5 €) вони лишаються знаками. <c>'\0'</c> — не двійник.
    /// Посеред слова брехню з будь-яким знаком поза білим списком сервер і так не прийме (<see cref="MarkInWord"/>); це
    /// прочитання — для країв слова («◯ДЯГ», «€ВА»): там знак буває й смайликом, тож не відмовляємо, а читаємо як літеру.
    /// </summary>
    static char SymbolTwin(int c) => c switch
    {
        '×' or '\u2715' or '\u2573' => 'х', // ✕ ╳
        '|' or '¦' or '∣' or '\u2502' or '\u23D0' or '\u05C0' => 'і', // │ ⏐ ׀ (рамка, риска, єврейський пасек)
        '\u25EF' or '\u25CB' or '\u00B0' or '\u2205' => 'о', // ◯ ○ ° ∅
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
    /// Чи стоїть посеред слова (між двома літерами, без пробілу) знак, що може вдавати літеру:
    /// «Д│СНЕЙЛЕНД», «МА✕ОРКА», «ЛЬВ◯ВІ», «Ап°ллон», «п∅льку». Двійників серед знаків не переловиш (рамки, геометрія,
    /// математика, чужа пунктуація), тож навпаки — білий список, як для літер: посеред слова можна лише дефіс, апостроф
    /// і звичайні розділові знаки (<see cref="WordMark"/>) — ними пишуть «Пд.Буг» чи «кіт/пес». Біля пробілу чи на краю
    /// брехні знаки й смайлики — як завгодно («кіт 🙂», «5 €», «2×2», «20°C»); відомих двійників там читає
    /// <see cref="SymbolTwin"/>. Перевіряється і як написано, і після NFKC.
    /// </summary>
    public static bool MarkInWord(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return Scan(s) || Scan(s.Normalize(NormalizationForm.FormKC));

        static bool Scan(string t)
        {
            var word = 0;          // що стоїть перед знаками: 0 — пробіл чи початок, 1 — цифра, 2 — літера
            var marks = false;     // після слова йдуть знаки
            var stray = false;     // серед них є не з білого списку
            foreach (var rune in t.EnumerateRunes())
            {
                var c = rune.Value;
                if (Invisible(c)) continue;
                // Наголос, з'єднувач смайлика, варіант гліфа — тримаються свого знака, а не стоять між.
                if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark) continue;
                if (Rune.IsLetterOrDigit(rune))
                {
                    var kind = Rune.IsLetter(rune) ? 2 : 1;
                    if (marks && stray && word == 2 && kind == 2) return true;
                    word = kind;
                    marks = stray = false;
                }
                else if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || Blank(c))
                {
                    word = 0;
                    marks = stray = false;
                }
                else if (word != 0)
                {
                    marks = true;
                    if (!WordMark(c)) stray = true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Знаки, яким можна стояти посеред слова: дефіси й тире, апострофи, крапка, кома, двокрапка, крапка з комою,
    /// знак питання, скісна, «&amp;», «+», підкреслення, лапки, три крапки. Жоден із них на картці не вдає літеру.
    /// Знак оклику й дужки — ні: посеред слова «!» вдає І («К!Т»), а «(» — С.
    /// </summary>
    static bool WordMark(int c) =>
        IsApostrophe(c)
        || c is '\'' or '-' or '.' or ',' or ':' or ';' or '?' or '/' or '&' or '+' or '_' or '"'
        || c is '«' or '»' or '„' or '“' or '”' or '‚' or '…' or '−'
        || c is >= 0x2010 and <= 0x2015;

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

    /// <summary>
    /// У слові, де є літери, цифри 0 і 3 — це О і З: «В0ДЕНЬ» на картці — те саме «ВОДЕНЬ». Лише група цифр із самих
    /// 0 і 3 («З00ПАРК»): «20метрів» і «35разів» — числа, склеєні зі словом, а не «2ометрів» і «з5разів».
    /// </summary>
    static void FoldDigitsInWords(StringBuilder sb)
    {
        var start = 0;
        for (var i = 0; i <= sb.Length; i++)
        {
            if (i < sb.Length && sb[i] is not (' ' or '-')) continue;
            var letter = false;
            for (var j = start; j < i && !letter; j++) letter = char.IsLetter(sb[j]);
            for (var j = start; letter && j < i;)
            {
                if (!char.IsDigit(sb[j]))
                {
                    j++;
                    continue;
                }
                var end = j;
                var looks = true;               // уся група цифр — самі 0 і 3
                for (; end < i && char.IsDigit(sb[end]); end++) looks &= sb[end] is '0' or '3';
                if (looks)
                    for (var x = j; x < end; x++) sb[x] = sb[x] == '0' ? 'о' : 'з';
                j = end;
            }
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
    public static List<string> Tokens(string? s) => Split(Norm(s));

    /// <summary>
    /// Уже нормалізований рядок — словами (<see cref="Tokens"/>). Без <paramref name="canon"/> службові слова лишаються як
    /// написано: для склеєного й розбитого, де «із» може бути й уламком слова («сл із» — це «сліз»).
    /// </summary>
    static List<string> Split(string norm, bool canon = true)
    {
        var list = new List<string>();
        foreach (var part in norm.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim('\'');
            if (t.Length > 0) list.Add(canon ? Canon(t) : t);
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

    /// <summary>
    /// Чи відстань Левенштейна між <paramref name="a"/> і <paramref name="b"/> не більша за <paramref name="k"/>. Без
    /// алокацій (рядки на стеку) і з раннім виходом: <see cref="TokenMatch"/> кличуть сотні разів на одну брехню.
    /// </summary>
    static bool LevWithin(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int k)
    {
        if (Math.Abs(a.Length - b.Length) > k) return false;
        Span<int> prev = b.Length < 128 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        Span<int> cur = b.Length < 128 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                cur[j] = v;
                if (v < best) best = v;
            }
            if (best > k) return false;        // далі відстань лише росте
            var t = prev;
            prev = cur;
            cur = t;
        }
        return prev[b.Length] <= k;
    }

    static int CommonPrefix(ReadOnlySpan<char> a, ReadOnlySpan<char> b) => a.CommonPrefixLength(b);

    static bool HasDigit(ReadOnlySpan<char> s)
    {
        foreach (var ch in s) if (char.IsDigit(ch)) return true;
        return false;
    }

    /// <summary>
    /// Одне слово правди — те саме? Грубе сито для <see cref="LooksTrue"/>: точно; одруківка (від чотирьох літер, одна
    /// правка); відмінок (спільний корінь із трьох літер, дві правки); довгий відмінок (спільний початок майже на все
    /// коротше слово, а різниця — не довша за закінчення: «кроликів»/«кролики», але не «hollywood»/«hollywoodhills»).
    /// Числа — лише точно: 1855 при правді 1854 — чесна брехня, а не одруківка. У слові з цифрами й літерами
    /// («35разів», склеєне з «35 разів») цифри — точно, а літери — з тим самим допуском.
    /// </summary>
    public static bool TokenMatch(string a, string b) => Match(a, b);

    static bool Match(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.SequenceEqual(b)) return true;
        if (Math.Abs(a.Length - b.Length) > 3) return false;
        return HasDigit(a) || HasDigit(b) ? RunsMatch(a, b) : Near(a, b);
    }

    /// <summary>Слова без цифр: <see cref="TokenMatch"/>.</summary>
    static bool Near(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.SequenceEqual(b)) return true;
        var min = Math.Min(a.Length, b.Length);
        if (min < 4) return false;
        var pre = CommonPrefix(a, b);
        if (LevWithin(a, b, pre >= 3 ? 2 : 1)) return true;
        return min >= 5 && pre >= Math.Max(4, min - 2) && Math.Abs(a.Length - b.Length) <= 3;
    }

    /// <summary>
    /// Слово з цифрами: по черзі шматки цифр і не-цифр; цифри мають збігтися точно, решта — як слова (<see cref="Near"/>).
    /// «35разив» = «35разів», але «4дні» ≠ «3дні» і «20метрів» ≠ «2метрів».
    /// </summary>
    static bool RunsMatch(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        while (a.Length > 0 && b.Length > 0)
        {
            var digits = char.IsDigit(a[0]);
            if (digits != char.IsDigit(b[0])) return false;
            int ra = Run(a, digits), rb = Run(b, digits);
            if (digits ? !a[..ra].SequenceEqual(b[..rb]) : !Near(a[..ra], b[..rb])) return false;
            a = a[ra..];
            b = b[rb..];
        }
        return a.Length == 0 && b.Length == 0;

        static int Run(ReadOnlySpan<char> s, bool digits)
        {
            var n = 1;
            while (n < s.Length && char.IsDigit(s[n]) == digits) n++;
            return n;
        }
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
        var glued = Glue.Of(Split(norm, canon: false));
        // Прочитання без «0 і 3 — літери»: склеєне «35разів» інакше стало б «з5разів» і не впізнало б «35 разів».
        var raw = Norm(lie, readDigits: false);
        var gluedRaw = raw == norm ? glued : Glue.Of(Split(raw, canon: false));
        foreach (var f in forms)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            var fn = Norm(f);
            if (norm.Length > 0 && norm == fn) return true;
            var ft = Split(fn);
            if (AllIn(ft, tokens)) return true;                     // правда всередині фрази: «звісно ж, на гасі»
            // Шматок правди: «Буг» для «Південний Буг». Шматок має бути щонайменше половиною змістовних слів форми:
            // інакше «фрукт» ставав би правдою через форму банку «овоч, а не фрукт».
            var meaningful = Meaningful(ft).Count;
            if (letters >= 3 && AllIn(tokens, ft) && tokens.Count * 2 >= meaningful) return true;
            // Те, що злилося б із правдою в одну картку (інше службове слово, закінчення), — теж правда.
            if (norm.Length > 0 && LooksSame(lie, f)) return true;
            // Склеєна чи розбита правда: «кістокмамонта», «Діс-ней-ленд», «ДІСНЕЙ.ЛЕНД», «на право сторонній рух».
            if (Glued(glued, Glue.Of(Split(fn, canon: false)), letters, meaningful)) return true;
            var fRaw = Norm(f, readDigits: false);
            if ((raw != norm || fRaw != fn) && Glued(gluedRaw, Glue.Of(Split(fRaw, canon: false)), letters, meaningful)) return true;
        }
        return false;
    }

    /// <summary>
    /// Правда, склеєна чи розбита як завгодно: уся правда стоїть у брехні (зайві слова довкола можна) або вся брехня —
    /// шматок правди на щонайменше половину змістовних слів (як у <see cref="LooksTrueAsWritten"/>).
    /// </summary>
    static bool Glued(Glue lie, Glue form, int letters, int meaningful)
    {
        if (Glue.Within(form, lie)) return true;
        if (letters < 3) return false;
        var covered = Glue.Piece(lie, form);
        return covered > 0 && covered * 2 >= meaningful;
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

    /// <summary>
    /// Слова одним суцільним рядком — без пробілів, дефісів і апострофів («д'і'с'н'е'й» — «дісней») — і межі слів у
    /// ньому: будь-які сусідні слова — зріз цього рядка, без алокацій. Для правди, яку склеїли («кістокмамонта») чи
    /// розбили («Діс-ней-ленд», «ДІСНЕЙ.ЛЕНД»): <see cref="Norm(string?)"/> ріже по пробілу й дефісу, тож окремими
    /// словами такої правди не впізнати.
    /// </summary>
    sealed class Glue
    {
        readonly string _text;
        readonly int[] _starts;     // початок кожного слова, а наприкінці — довжина рядка

        Glue(string text, int[] starts)
        {
            _text = text;
            _starts = starts;
        }

        public int Count => _starts.Length - 1;

        public static Glue Of(List<string> words)
        {
            var sb = new StringBuilder();
            var starts = new List<int>(words.Count + 1);
            foreach (var w in words)
            {
                var start = sb.Length;
                foreach (var ch in w)
                    if (ch != '\'') sb.Append(ch);
                if (sb.Length > start) starts.Add(start);
            }
            starts.Add(sb.Length);
            return new Glue(sb.ToString(), [.. starts]);
        }

        int Len(int i, int n) => _starts[i + n] - _starts[i];

        ReadOnlySpan<char> Words(int i, int n) => _text.AsSpan(_starts[i], Len(i, n));

        /// <summary>
        /// Сусідні слова правди <paramref name="truth"/>[i, i+a) і брехні <paramref name="lie"/>[j, j+b) — те саме?
        /// <list type="bullet">
        /// <item>Слово проти слова — <see cref="TokenMatch"/>, як завжди (і службові слова в різних виглядах).</item>
        /// <item>Одне слово правди проти кількох слів брехні — це уламки слова правди («Діз-ней-ленд»): склеєні, вони
        /// мають бути цим словом з тим самим допуском (<see cref="TokenMatch"/>).</item>
        /// <item>Кілька слів правди проти одного слова брехні — це склеєна правда («кістокмамонту»): ріжемо слово брехні
        /// на шматки, і кожен має бути своїм словом правди (<see cref="Cut"/>). Цілим словом тут звіряти не можна: допуск
        /// на одруківку відкусив би ціле коротке слово правди («очетвертій» проти «четвертій» у «10 четвертій»).</item>
        /// <item>Кілька проти кількох (межа посунулась: «направо сторонній» і «на правосторонній») — лише точно. Інакше
        /// «кит і пес» через спільний допуск став би правдою «кіт і пес», а короткі слова ми звіряємо лише точно.</item>
        /// </list>
        /// </summary>
        static bool Same(Glue truth, int i, int a, Glue lie, int j, int b)
        {
            var ts = truth.Words(i, a);
            var ls = lie.Words(j, b);
            if (a > 1) return b > 1 ? ts.SequenceEqual(ls) : Cut(ls, truth, i, a);
            if (b == 1 || ls.SequenceEqual(ts)) return Match(ts, ls) || b == 1 && Service(ts, ls);
            // Уламки з допуском: крайні уламки мусять брати щонайменше по дві літери правди. Інакше «Південний Бог» при
            // правді «південний» зійшлося б через «довгий відмінок», хоч «Бог» — окреме слово, а не шматок правди.
            return ls.Length - lie.Len(j + b - 1, 1) <= ts.Length - 2
                && ls.Length - lie.Len(j, 1) <= ts.Length - 2
                && Match(ts, ls);
        }

        /// <summary>
        /// Чи можуть блоки такої довжини бути тим самим (<see cref="Same"/>): кілька проти кількох — однакова довжина,
        /// інакше — щонайбільше три знаки різниці на слово (стільки дає <see cref="TokenMatch"/>).
        /// </summary>
        static bool Fits(int la, int a, int lb, int b) =>
            a > 1 && b > 1 ? la == lb : Math.Abs(la - lb) <= 3 * Math.Max(a, b);

        /// <summary>
        /// Одне слово <paramref name="one"/> — це слова <paramref name="many"/>[i, i+k), склеєні: ріжемо його на k
        /// шматків, і кожен шматок має бути своїм словом (<see cref="TokenMatch"/>, з відмінками й одруківками).
        /// «кістокмамонту» = «кісток» + «мамонту», «35разив» = «35» + «разив».
        /// </summary>
        static bool Cut(ReadOnlySpan<char> one, Glue many, int i, int k)
        {
            // at[p]: перші t слів можна вирізати з one[..p]. Динаміка, а не перебір: шматків — до семи довжин на слово.
            Span<bool> at = one.Length < 128 ? stackalloc bool[one.Length + 1] : new bool[one.Length + 1];
            Span<bool> next = one.Length < 128 ? stackalloc bool[one.Length + 1] : new bool[one.Length + 1];
            at[0] = true;
            for (var t = 0; t < k; t++)
            {
                var word = many.Words(i + t, 1);
                var last = t == k - 1;
                next.Clear();
                var any = false;
                for (var p = 0; p < one.Length; p++)
                {
                    if (!at[p]) continue;
                    if (last)
                    {
                        if (Match(one[p..], word) || Service(one[p..], word)) return true;
                        continue;
                    }
                    var to = Math.Min(one.Length - p - (k - 1 - t), word.Length + 3);
                    for (var cut = Math.Max(1, word.Length - 3); cut <= to; cut++)
                        if (Match(one.Slice(p, cut), word) || Service(one.Slice(p, cut), word)) next[p + cut] = any = true;
                }
                if (!any) return false;
                var swap = at;
                at = next;
                next = swap;
            }
            return false;
        }

        /// <summary>
        /// Одне службове слово в різних виглядах (<see cref="Canon"/>: у/в, з/із/зі/зо, і/й): «страхуйжаху» — це «страху» +
        /// «й» + «жаху», а правда — «страху і жаху».
        /// </summary>
        static bool Service(ReadOnlySpan<char> a, ReadOnlySpan<char> b) =>
            a.Length <= 2 && b.Length <= 2 && Canon(a.ToString()) == Canon(b.ToString());

        /// <summary>
        /// Чи всі слова правди <paramref name="need"/> стоять у брехні <paramref name="have"/> по порядку, коли сусідні
        /// слова з будь-якого боку можна склеїти, а зайві слова брехні — пропустити: «звісно ж, Діс-ней-ленд».
        /// </summary>
        public static bool Within(Glue need, Glue have)
        {
            int m = need.Count, n = have.Count;
            if (m == 0 || n == 0) return false;
            var can = new bool[m + 1, n + 1];      // can[i, j]: слова need від i-го вкладаються в слова have від j-го
            for (var j = 0; j <= n; j++) can[m, j] = true;
            for (var i = m - 1; i >= 0; i--)
                for (var j = n - 1; j >= 0; j--)
                {
                    var ok = can[i, j + 1];         // слово have зайве
                    for (var a = 1; !ok && i + a <= m; a++)
                    {
                        var la = need.Len(i, a);
                        for (var b = 1; j + b <= n; b++)
                        {
                            if (!can[i + a, j + b] || !Fits(la, a, have.Len(j, b), b)) continue;
                            if (Same(need, i, a, have, j, b))
                            {
                                ok = true;
                                break;
                            }
                        }
                    }
                    can[i, j] = ok;
                }
            return can[0, 0];
        }

        /// <summary>
        /// Уся брехня <paramref name="part"/> — шматок правди <paramref name="whole"/>: кожне її слово (чи кілька
        /// склеєних) по порядку знаходить своє місце в правді, зайві слова правди можна пропустити.
        /// Повертає, скільки слів <paramref name="whole"/> покрито (найбільше з можливих), або −1, коли ніяк.
        /// </summary>
        public static int Piece(Glue part, Glue whole)
        {
            int n = part.Count, m = whole.Count;
            if (n == 0 || m == 0) return -1;
            var best = new int[n + 1, m + 1];      // best[j, i]: part від j-го слова — у whole від i-го; best[n, *] = 0
            for (var j = 0; j < n; j++) best[j, m] = -1;
            for (var j = n - 1; j >= 0; j--)
                for (var i = m - 1; i >= 0; i--)
                {
                    var v = best[j, i + 1];         // слово whole пропущено
                    for (var b = 1; j + b <= n; b++)
                    {
                        var lb = part.Len(j, b);
                        for (var a = 1; i + a <= m; a++)
                        {
                            var after = best[j + b, i + a];
                            if (after < 0 || a + after <= v || !Fits(lb, b, whole.Len(i, a), a)) continue;
                            if (Same(whole, i, a, part, j, b)) v = a + after;
                        }
                    }
                    best[j, i] = v;
                }
            return best[0, 0];
        }
    }
}
