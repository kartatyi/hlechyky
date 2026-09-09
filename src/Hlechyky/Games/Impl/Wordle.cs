using System.Text;
using System.Text.Json;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Глек-слово: одне п'ятилітерне слово на день — спільне для всіх, шість спроб на кожного. Зелена
/// плитка — літера на своєму місці, жовта — така літера в слові є, але стоїть інде, чорна — нема
/// зовсім.
///
/// Кімната тут не стіл, а особиста шухляда: <c>Private</c> (у лобі її нема, дивитись може лише
/// господар), <c>Persistent</c> (стан переживає закриття вкладки), ключ — <c>daily:wordle:день:нік</c>,
/// саме з нього сервіси дістають гру й день (specs/daily.md). Слово дня беремо з
/// <see cref="Words.Daily5ForDay"/>: список відповідей один раз перемішано сталою перестановкою, тож
/// повтор трапиться не раніше ніж через чотири роки.
/// </summary>
public sealed class Wordle : Game, IDailyGame
{
    /// <summary>Скільки літер у слові. Разом із <see cref="Words.IsValid5"/> — п'ять, і це не налаштовується.</summary>
    public const int Len = 5;

    /// <summary>Скільки спроб дається на день.</summary>
    public const int MaxTries = 6;

    /// <summary>Save/Load пишемо тим самим camelCase, що й види: стан читається очима в базі й у тестах.</summary>
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>Збережений стан дня (specs/wordle.md). Прапорці тут для очей — при завантаженні їх перераховуємо.</summary>
    sealed record Saved(string? Day, string[]? Guesses, bool Solved, bool Failed);

    readonly List<string> _guesses = [];

    Words? _words;
    /// <summary>День за Києвом, на який зараз грають; порожній — Start ще не був.</summary>
    string _day = "";
    /// <summary>Слово дня. Порожній рядок — словника нема, і про це треба сказати, а не впасти.</summary>
    string _answer = "";
    bool _solved;
    bool _failed;
    /// <summary>Соло-результат і щоденну нагороду за цей день уже відправлено — двічі не платимо.</summary>
    bool _reported;
    /// <summary>Рядок у спільний Журнал за цей день уже пішов — удруге не кричимо (див. <see cref="Close"/>).</summary>
    bool _announced;

    public override GameInfo Info { get; } = new(
        "wordle", "Глек-слово", "Глек-слово", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.LowerIsBetter,
        Hint: "П'ять літер, шість спроб. Одне слово на день — на всіх. Зелена — на місці, жовта — є, але не тут");

    /// <summary>Ключ особистої кімнати: рівно та форма, з якої WP1 дістає гру й день (specs/daily.md).</summary>
    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    /// <summary>
    /// Кімната тут — це головоломка одного дня, і саме той день стоїть у її ключі. Тому день ставимо
    /// раз і назавжди: «Ще раз» щоденної гри нового слова не дає (а після півночі не має підмінювати
    /// вчорашню кімнату сьогоднішньою — по нове слово люди приходять із панелі, і каркас відкриє їм
    /// кімнату з новим ключем). Інакше кнопка «Ще раз», яку каркас малює всім соло-іграм,
    /// перетворювала б шість спроб на нескінченні.
    /// </summary>
    public override void Start()
    {
        // Сервіс беремо тут, а не в конструкторі: ігри створює реєстр рефлексією, без параметрів.
        // GetService, а не GetRequiredService: без словника гра має сказати «нема словника», а не
        // покласти кімнату («партія зламалась, вибачте»).
        _words ??= Ctx.Services.GetService<Words>();
        if (_day.Length == 0) _day = Days.Today(Ctx.Clock);
        // Слово перечитуємо щоразу: словники могли доїхати вже після того, як кімнату відкрили.
        _answer = _words?.Daily5ForDay(_day) ?? "";

        // «Ще раз» на вже дограному дні: одразу закриваємо партію, щоб картка не вдавала, ніби
        // спроби ще лишились.
        if (_solved || _failed) Close();
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "guess") return ActResult.Fail("Тут так не ходять");
        if (_answer.Length != Len) return ActResult.Fail("Словника нема — сьогодні без слова");
        if (_solved || _failed) return ActResult.Fail("Сьогоднішнє слово вже позаду. Приходь завтра");
        if (_guesses.Count >= MaxTries) return ActResult.Fail("Спроби на сьогодні скінчились");

        if (Word(payload) is not { } raw) return ActResult.Fail("Не зрозумів, що за слово");
        var word = Words.Normalize(raw);
        if (word is null || word.Length != Len) return ActResult.Fail("Треба рівно п'ять українських літер");
        if (_words?.IsValid5(word) != true) return ActResult.Fail("Такого слова не знаю");

        _guesses.Add(word);
        if (word == _answer)
        {
            _solved = true;
            Close();
            return ActResult.Accept("Оце так!");
        }
        if (_guesses.Count >= MaxTries)
        {
            _failed = true;
            Close();
            return ActResult.Done;
        }
        return ActResult.Done;
    }

    public override object View(int? seat)
    {
        var over = _solved || _failed;
        return new
        {
            day = _day,
            no = _day.Length == 0 ? 0 : Days.Number(_day),
            rows = _guesses.Select(w => new { word = w, marks = Marks(_answer, w) }).ToArray(),
            attempts = _guesses.Count,
            max = MaxTries,
            solved = _solved,
            failed = _failed,
            // до кінця дня відповіді у виді нема взагалі — інакше її видно в консолі браузера
            answer = over ? _answer : null,
            keys = Keys(),
            share = over ? Share() : null,
            // поза spec, але без цього гравець дивиться на порожню дошку й не розуміє, чому нічого не
            // приймається: словників у цій збірці може просто не бути (INTEGRATION-NOTES §4)
            noWords = _answer.Length != Len,
        };
    }

    public override string? Save() =>
        _day.Length == 0 ? null : JsonSerializer.Serialize(new Saved(_day, [.. _guesses], _solved, _failed), Wire);

    /// <summary>
    /// Відновлення дня. Чужий день — не наша справа: лишаємо чисту дошку (слово вже інше). Прапорці зі
    /// сховища не читаємо, а перераховуємо зі спроб: так зіпсований запис не подарує розв'язаного дня.
    /// </summary>
    public override void Load(string json)
    {
        Saved? saved;
        try { saved = JsonSerializer.Deserialize<Saved>(json, Wire); }
        catch (JsonException) { return; }
        if (saved is null || saved.Day != _day || _answer.Length != Len) return;

        _guesses.Clear();
        foreach (var raw in saved.Guesses ?? [])
        {
            if (_guesses.Count >= MaxTries) break;
            if (Words.Normalize(raw) is not { Length: Len } w) continue;
            _guesses.Add(w);
            // відповідь у списку — це кінець дня; усе, що стоїть за нею, у грі статись не могло, тож
            // хвіст відрізаємо. Інакше зіпсований запис підняв би розв'язаний день як недограний, і
            // людина «вгадала» б його ще раз — уже з більшою кількістю спроб.
            if (w == _answer) break;
        }
        _solved = _guesses.Count > 0 && _guesses[^1] == _answer;
        _failed = !_solved && _guesses.Count >= MaxTries;
        // результат за цей день уже пішов у сервіси тоді, коли слово справді вгадали
        _reported = _solved;
        if (_solved || _failed) Close();
    }

    // ------------------------------------------------------------------------------------- правила

    /// <summary>
    /// Класична оцінка спроби: спершу розставляємо зелені, і лише те, що лишилось від літер відповіді,
    /// роздаємо жовтим зліва направо. Через це в спробі «лілія» проти відповіді з однією «і» жовтою
    /// стане тільки перша «і», а не всі три — саме так рахує оригінал.
    /// </summary>
    /// <returns>Рядок із <see cref="Len"/> символів: G — на місці, Y — є, але не тут, B — нема.</returns>
    public static string Marks(string? answer, string? guess)
    {
        if (answer is not { Length: Len } || guess is not { Length: Len }) return new string('B', Len);

        var marks = new char[Len];
        var left = new Dictionary<char, int>();
        for (var i = 0; i < Len; i++)
        {
            if (guess[i] == answer[i]) marks[i] = 'G';
            else
            {
                marks[i] = 'B';
                left[answer[i]] = left.GetValueOrDefault(answer[i]) + 1;
            }
        }
        for (var i = 0; i < Len; i++)
        {
            if (marks[i] == 'G') continue;
            if (left.GetValueOrDefault(guess[i]) <= 0) continue;
            marks[i] = 'Y';
            left[guess[i]]--;
        }
        return new string(marks);
    }

    /// <summary>Зведений стан літер для екранної клавіатури: зелена перекриває жовту, жовта — чорну.</summary>
    Dictionary<string, string> Keys()
    {
        var best = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in _guesses)
        {
            var marks = Marks(_answer, w);
            for (var i = 0; i < Len; i++)
            {
                var key = w[i].ToString();
                var rank = marks[i] switch { 'G' => 3, 'Y' => 2, _ => 1 };
                var was = best.TryGetValue(key, out var s) ? s switch { "G" => 3, "Y" => 2, _ => 1 } : 0;
                if (rank > was) best[key] = marks[i].ToString();
            }
        }
        return best;
    }

    /// <summary>Сітка емодзі для кнопки «Скопіювати»: без жодної літери, тож ділитись нею не соромно.</summary>
    string Share()
    {
        var sb = new StringBuilder();
        sb.Append("Глек-слово #").Append(_day.Length == 0 ? 0 : Days.Number(_day)).Append(' ')
          .Append(_solved ? _guesses.Count.ToString() : "X").Append('/').Append(MaxTries);
        foreach (var w in _guesses)
        {
            sb.Append('\n');
            foreach (var m in Marks(_answer, w)) sb.Append(m switch { 'G' => "🟩", 'Y' => "🟨", _ => "⬛" });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Партія дня скінчилась. Соло-результат, щоденну нагороду і рядок Журналу шлемо рівно раз:
    /// <see cref="Load"/> і «Ще раз» кличуть це саме на вже зіграному дні. Платити вдруге за той самий
    /// день нема за що, а оголошувати — тим паче: «Ще раз» на дограному дні каркас пускає скільки
    /// завгодно разів (у <c>core.js</c> кнопки для щоденних нема, але метод хаба відкритий), і без
    /// прапорця одна людина залила б спільний Журнал усім. Нуль черепків у <c>Award</c> — не «нічого»,
    /// а «плати типову щоденну» (specs/daily.md).
    /// </summary>
    void Close()
    {
        if (_solved && !_reported)
        {
            _reported = true;
            Ctx.Score(0, _guesses.Count);
            Ctx.Award(0, 0, $"daily:{Info.Id}");
        }
        // Теперішній час у рядку Журналу — щоб не вгадувати рід ніка («вгадав»/«вгадала»).
        // Програш не оголошуємо: у спільних Балачках це нікому не свято.
        var log = _solved && !_announced
            ? $"{Info.Title}: {Ctx.NickOf(0)} вгадує слово дня з {Ordinal(_guesses.Count)} спроби"
            : "";
        _announced = true;
        Ctx.Finish(_solved ? [0] : [], log);
    }

    static string Ordinal(int n) => n switch
    {
        1 => "першої",
        2 => "другої",
        3 => "третьої",
        4 => "четвертої",
        5 => "п'ятої",
        6 => "шостої",
        _ => $"{n}-ї",
    };

    /// <summary>Спробу приймаємо і як <c>{word:"глечик"}</c>, і як голий рядок — клієнтам так простіше.</summary>
    static string? Word(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.String => payload.GetString(),
        JsonValueKind.Object when payload.TryGetProperty("word", out var w) && w.ValueKind == JsonValueKind.String => w.GetString(),
        _ => null,
    };
}
