using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Спільне серце Клавоперегонів — і столу (<see cref="Typerace"/>), і тренування (<see cref="TyperaceSolo"/>):
/// гонщики, текст, відлік, друк (<c>pos</c>), фініш із суддею, кадр і вид. Правила кінця партії в них різні, тож
/// їх кожна гра дописує сама (<see cref="OnRaceTick"/>, <see cref="OnVerified"/>).
/// <para>
/// Фізики тут нема — лише стан друку: <c>c</c> (скільки правильно надруковано), чи висить червоний, помилки й час.
/// Свій трактор клієнт рахує сам (миттєво), сервер лише зберігає присланий <c>c</c> і показує іншим; офіційний
/// час, місця, кінець заїзду й зарахованість — лише тут.
/// </para>
/// </summary>
public abstract class TyperaceRace : Game
{
    // Числа контракту (spec §2) — міняти разом зі spec і typerace.js.
    public const int TickMs = 200;
    public const int ReadyMs = 3000;
    /// <summary>Скільки тиків триває відлік: 3 с по 200 мс.</summary>
    public const int ReadyTicks = ReadyMs / TickMs;
    public const int HardCapBaseMs = 60_000;
    public const int HardCapPerCharMs = 500;

    public const string PhaseLobby = "lobby", PhasePick = "pick", PhaseReady = "ready", PhaseGo = "go", PhaseDone = "done";

    public const string ActPos = "pos", ActFinish = "finish";

    /// <summary>Гонщик на одному місці. Порожнє місце — <see cref="In"/> false.</summary>
    protected sealed class Racer
    {
        public bool In;              // сидів на старті заїзду
        public string? Nick;         // нік на старті: хто встав, лишається в підсумку під своїм ім'ям
        public bool Gone;            // встав посеред заїзду
        public int C;                // правильно надруковано (останній pos, на фініші — len)
        public bool Red;             // висить червоний
        public int Wrong;            // помилок: із журналу на фініші; до фінішу — скільки разів pos показав червоний
        public long? Fin;            // офіційний час, мс від старту
        public int? Place;           // місце серед зарахованих
        public int? Cpm, Acc;
        public string? Flag;         // null — зараховано (або ще не фінішував)
        public int Seat;
        /// <summary>Коли (мс від старту) pos останній раз перетнув ¼, ½, ¾ тексту; -1 — ще ні (суддя, перевірка 8).</summary>
        public readonly long[] Seen = new long[TyperaceJudge.SeenMarks];
        /// <summary>Остання зміна з pos — «ще друкує» (дотяжка хвоста).</summary>
        public DateTimeOffset LastMove;
        /// <summary>Де помилявся (з журналу зарахованого фінішу) — для слова-пастки; null — журналу нема.</summary>
        public bool[]? Missed;
        public DateTimeOffset LastCheer;

        public int S => Gone ? 3 : Fin is not null ? 2 : Red ? 1 : 0;

        public void Reset(int seat, bool seated, string? nick)
        {
            Seat = seat;
            In = seated;
            Nick = nick;
            Gone = false;
            C = 0;
            Red = false;
            Wrong = 0;
            Fin = null;
            Place = null;
            Cpm = Acc = null;
            Flag = null;
            Array.Fill(Seen, -1L);
            LastMove = default;
            Missed = null;
            LastCheer = default;
        }
    }

    /// <summary>Підсумок заїзду для виду (<c>result</c>); <paramref name="Trap"/> — слово-пастка, якщо було.</summary>
    protected sealed record Summary(int[] Order, int[] Winners, string Say, bool AllDone, TyperaceTrap? Trap = null);

    protected Racer[] Racers = [];
    protected TyperaceBank Bank = null!;
    /// <summary>«Наші балачки» (null — сервісу нема, як у старих тестах: тоді джерело «балачки» їде класикою).</summary>
    protected TyperaceChat? ChatBank;
    /// <summary>Чому цей заїзд не з Балачок, хоч просили (замало реплік, ще дочитуємо) — рядок під текстом; null — усе гаразд.</summary>
    protected string? SrcNote;
    protected string Length = TyperaceBank.Medium;
    protected string Source = TyperaceBank.All;
    /// <summary>Пам'ять кімнати: які записи банку вже були (spec §7.4).</summary>
    protected readonly HashSet<string> Used = new(StringComparer.Ordinal);

    protected string Phase = PhaseLobby;
    protected string? Text;
    protected TyperaceSource? Src;
    protected int Len;
    protected DateTimeOffset? ReadyAt, GoAt, EndsAt;
    protected bool TailSet;
    protected int Places;
    /// <summary>Хтось натиснув хоч щось (pos із c &gt; 0 чи червоним) — інакше хвилина тиші закриває заїзд.</summary>
    protected bool Moved;
    protected int TickNo;
    protected Summary? Result;
    protected bool ViewDirty, FrameDirty;

    /// <summary>Скільки місць у грі (стіл — 10, соло — 1).</summary>
    protected int Seats => Info.MaxPlayers;

    public override string SeatName(int seat) => (seat + 1).ToString();

    /// <summary>Банк: свій із сервісів (тести), інакше — з файла гри.</summary>
    protected void TakeBank()
    {
        Bank = Ctx.Services.GetService<TyperaceBank>() ?? TyperaceBank.Default;
        ChatBank = Ctx.Services.GetService<TyperaceChat>();
        ChatBank?.Warm();   // з Configure — поза замком; сама вибірка однаково йде фоном
        if (Racers.Length != Seats)
        {
            Racers = new Racer[Seats];
            for (var i = 0; i < Seats; i++) Racers[i] = new Racer { Seat = i };
        }
    }

    protected static string OptionOr(IReadOnlyDictionary<string, string> options, string key, string[] allowed, string fallback) =>
        options.TryGetValue(key, out var v) && Array.IndexOf(allowed, v) >= 0 ? v : fallback;

    /// <summary>Узяти новий текст і почати відлік. false — банк порожній.</summary>
    protected bool BeginReady()
    {
        var pick = PickText();
        if (pick is null) return false;
        Text = pick.Text;
        Src = pick.Src;
        Len = pick.Text.Length;
        Phase = PhaseReady;
        ReadyAt = Ctx.Clock.UtcNow;
        GoAt = EndsAt = null;
        TailSet = false;
        Places = 0;
        Moved = false;
        TickNo = 0;
        Result = null;
        for (var i = 0; i < Racers.Length; i++) Racers[i].Reset(i, Ctx.Seated(i), Ctx.NickOf(i));
        ViewDirty = true;
        FrameDirty = false;
        return true;
    }

    /// <summary>
    /// Текст заїзду. «Наші балачки» — з пам'яті <see cref="TyperaceChat"/>; «мікс» — навпіл з рештою банку. Реплік
    /// замало (чи ще дочитуються) — їдемо класикою, а <see cref="SrcNote"/> пояснює чому.
    /// </summary>
    TyperacePick? PickText()
    {
        SrcNote = null;
        var source = Source;
        if (source is TyperaceChat.Chat or TyperaceChat.Mix)
        {
            var lines = ChatBank?.Lines ?? [];
            var wantChat = source == TyperaceChat.Chat || Ctx.Rng.Next(2) == 0;
            if (lines.Count >= TyperaceChat.MinLines)
            {
                if (wantChat) return TyperaceChat.Pick(lines, Length, Ctx.Rng, Used);
                source = TyperaceBank.All;
            }
            else
            {
                SrcNote = ChatBank is { Loaded: false }
                    ? "💬 Балачки ще дочитуються — цей заїзд класикою, наступний уже з наших"
                    : $"💬 У Балачках поки замало реплік для заїзду ({lines.Count} з {TyperaceChat.MinLines}: беремо лише від власників акаунтів, без посилань, латиниці й лайки) — їдемо класикою";
                source = source == TyperaceChat.Chat ? TyperaceBank.Classic : TyperaceBank.All;
            }
        }
        return Bank.Pick(Length, source, Ctx.Rng, Used);
    }

    /// <summary>Стеля партії: 60 с + 0,5 с на знак.</summary>
    public static long HardCapMs(int len) => HardCapBaseMs + (long)len * HardCapPerCharMs;

    public override TickResult Tick()
    {
        var now = Ctx.Clock.UtcNow;
        if (Phase == PhaseReady && ReadyAt is { } r && now >= r.AddMilliseconds(ReadyMs))
        {
            Phase = PhaseGo;
            GoAt = now;
            EndsAt = now.AddMilliseconds(HardCapMs(Len));
            TickNo = 0;
            ViewDirty = true;
        }
        else if (Phase == PhaseGo)
        {
            TickNo++;
            OnRaceTick(now);
        }
        var result = new TickResult(FrameDirty && Phase == PhaseGo && SendsFrames, ViewDirty);
        FrameDirty = false;
        ViewDirty = false;
        return result;
    }

    /// <summary>Чи слати кадри: у соло дивитись нікому, а свій трактор клієнт і так знає.</summary>
    protected virtual bool SendsFrames => true;

    /// <summary>Тик посеред заїзду: чи не пора закінчувати (стеля, хвіст, усі доїхали, хвилина тиші).</summary>
    protected abstract void OnRaceTick(DateTimeOffset now);

    /// <summary>Зарахований фініш: місце, очки, ачівки, хвіст — у кожної гри своє.</summary>
    protected abstract void OnVerified(Racer r, DateTimeOffset now);

    /// <summary>Незарахований фініш (🤖).</summary>
    protected virtual void OnFlagged(Racer r, DateTimeOffset now) { }

    /// <summary>Чи всі, хто їде (сидів на старті й не встав), уже на фініші.</summary>
    protected bool AllFinished()
    {
        var any = false;
        foreach (var r in Racers)
        {
            if (!r.In || r.Gone) continue;
            any = true;
            if (r.Fin is null) return false;
        }
        return any;
    }

    public override ActResult Act(int seat, string action, JsonElement payload) => action switch
    {
        ActPos => Pos(seat, payload),
        ActFinish => Finish(seat, payload),
        _ => ActResult.Fail("Тут так не ходять"),
    };

    ActResult Pos(int seat, JsonElement payload)
    {
        if (Phase != PhaseGo) return ActResult.Fail(Phase == PhaseDone ? "Перегони вже скінчились" : "Ще не старт");
        if (seat < 0 || seat >= Racers.Length) return ActResult.Fail("Ти в цих перегонах не їдеш");
        var r = Racers[seat];
        if (!r.In || r.Gone) return ActResult.Fail("Ти в цих перегонах не їдеш");
        if (r.Fin is not null) return ActResult.Fail("Ти вже на фініші");
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("c", out var cEl) || cEl.ValueKind != JsonValueKind.Number || !cEl.TryGetInt32(out var c)
            || !payload.TryGetProperty("e", out var eEl) || eEl.ValueKind != JsonValueKind.Number || !eEl.TryGetInt32(out var e)
            || c < 0 || c > Len || e is not (0 or 1))
            return ActResult.Fail("Не зрозумів, де ти");
        var red = e == 1;
        if (c == r.C && red == r.Red) return ActResult.Done;
        if (red && !r.Red) r.Wrong++;
        // відмітки ¼, ½, ¾ для судді: коли перетнув востаннє (відступив назад — забуваємо, перетне ще раз)
        var now = Ctx.Clock.UtcNow;
        var ms = (long)(now - GoAt!.Value).TotalMilliseconds;
        for (var q = 0; q < TyperaceJudge.SeenMarks; q++)
        {
            var mark = TyperaceJudge.Mark(Len, q);
            if (c < mark) r.Seen[q] = -1;
            else if (r.C < mark) r.Seen[q] = ms;
        }
        r.LastMove = now;
        r.C = c;
        r.Red = red;
        if (c > 0 || red) Moved = true;
        FrameDirty = true;
        return ActResult.Done;
    }

    ActResult Finish(int seat, JsonElement payload)
    {
        if (Phase is PhaseLobby or PhasePick or PhaseReady) return ActResult.Fail("Перегони ще не почались");
        if (Phase == PhaseDone) return ActResult.Fail("Перегони вже скінчились");
        if (seat < 0 || seat >= Racers.Length) return ActResult.Fail("Ти в цих перегонах не їдеш");
        var r = Racers[seat];
        if (!r.In || r.Gone) return ActResult.Fail("Ти в цих перегонах не їдеш");
        if (r.Fin is not null) return ActResult.Fail("Ти вже на фініші");

        // k/d, яких нема чи які не рядки, — це не відмова, а «журнал не читається»: фініш однаково зараховується як фініш
        string? k = null, d = null;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("k", out var kEl) && kEl.ValueKind == JsonValueKind.String) k = kEl.GetString();
            if (payload.TryGetProperty("d", out var dEl) && dEl.ValueKind == JsonValueKind.String) d = dEl.GetString();
        }
        var now = Ctx.Clock.UtcNow;
        var ms = Math.Max(1L, (long)Math.Round((now - GoAt!.Value).TotalMilliseconds));
        var v = TyperaceJudge.Check(Len, k ?? "", d, ms, r.Seen);
        r.Fin = ms;
        r.C = Len;
        r.Red = false;
        r.Flag = v.Flag;
        r.Wrong = v.Wrong;
        r.Cpm = (int)Math.Round(Len * 60_000.0 / ms, MidpointRounding.AwayFromZero);
        r.Acc = v.Correct + v.Wrong > 0 ? (int)Math.Round(100.0 * v.Correct / (v.Correct + v.Wrong), MidpointRounding.AwayFromZero) : null;
        Moved = true;
        FrameDirty = true;
        ViewDirty = true;
        if (v.Ok)
        {
            r.Missed = new bool[Len];
            TyperaceJudge.Misses(k, Len, r.Missed);
            r.Place = ++Places;
            OnVerified(r, now);
            return ActResult.Accept(FinishText(r));
        }
        OnFlagged(r, now);
        return ActResult.Accept(FlaggedText(r));
    }

    protected virtual string FinishText(Racer r) => $"Фініш! {r.Cpm} зн/хв, точність {r.Acc} %";

    protected virtual string FlaggedText(Racer r) => $"Фініш, але заїзд не зараховано: {TyperaceJudge.Reason(r.Flag)}";

    /// <summary>Ачівки за зарахований заїзд (spec §2.3) — і за столом, і в соло.</summary>
    protected void AwardAchievements(Racer r)
    {
        if (r.Cpm >= 300 && r.Acc >= 95 && Len >= 120) Ctx.Award(r.Seat, 0, "ach:typerace-300");
        if (r.Wrong == 0 && Len >= 240) Ctx.Award(r.Seat, 0, "ach:typerace-clean");
    }

    /// <summary>
    /// Хто не доїхав — своїми знаками на хвилину за час заїзду (показуємо в підсумку, але не зараховуємо нікуди).
    /// </summary>
    protected void FillUnfinished(DateTimeOffset now)
    {
        var raceMs = GoAt is { } g ? Math.Max(1000.0, (now - g).TotalMilliseconds) : 1000.0;
        foreach (var r in Racers)
            if (r.In && r.Fin is null)
            {
                r.Cpm = (int)Math.Round(r.C * 60_000.0 / raceMs, MidpointRounding.AwayFromZero);
                r.Red = false;
            }
    }

    /// <summary>
    /// Порядок підсумку: зараховані за місцем, далі 🤖 за часом, далі ті, хто не дописав, — за пройденим (більше
    /// вище), при рівному — менше помилок, далі за номером місця.
    /// </summary>
    protected int[] Standings()
    {
        var list = new List<Racer>();
        foreach (var r in Racers) if (r.In) list.Add(r);
        list.Sort((a, b) =>
        {
            int Group(Racer x) => x.Place is not null ? 0 : x.Fin is not null ? 1 : 2;
            var ga = Group(a);
            var gb = Group(b);
            if (ga != gb) return ga.CompareTo(gb);
            if (ga == 0) return a.Place!.Value.CompareTo(b.Place!.Value);
            if (ga == 1) return a.Fin!.Value != b.Fin!.Value ? a.Fin.Value.CompareTo(b.Fin.Value) : a.Seat.CompareTo(b.Seat);
            if (a.C != b.C) return b.C.CompareTo(a.C);
            if (a.Wrong != b.Wrong) return a.Wrong.CompareTo(b.Wrong);
            return a.Seat.CompareTo(b.Seat);
        });
        var order = new int[list.Count];
        for (var i = 0; i < list.Count; i++) order[i] = list[i].Seat;
        return order;
    }

    protected int Percent(Racer r) => Len > 0 ? (int)Math.Floor(100.0 * r.C / Len) : 0;

    public override object? Frame() => new { t = TickNo, p = Positions() };

    /// <summary>Пласкі пари «c, s» за місцями (порожнє — -1, -1).</summary>
    protected int[] Positions()
    {
        var p = new int[2 * Racers.Length];
        for (var i = 0; i < Racers.Length; i++)
        {
            var r = Racers[i];
            if (!r.In) { p[2 * i] = -1; p[2 * i + 1] = -1; continue; }
            p[2 * i] = r.C;
            p[2 * i + 1] = r.S;
        }
        return p;
    }

    /// <summary>Лобі наново: стіл дограли, і хтось сів — каркас відкрив кімнату знову, а Start() ще не було.</summary>
    protected virtual bool LobbyNow => Phase == PhaseLobby;

    protected object RacerView(Racer r) => new
    {
        seat = r.Seat,
        nick = r.Nick ?? Ctx.NickOf(r.Seat) ?? "",
        gone = r.Gone,
        c = r.C,
        s = r.S,
        wrong = r.Wrong,
        fin = r.Fin,
        place = r.Place,
        cpm = r.Cpm,
        acc = r.Acc,
        flag = r.Flag,
    };

    protected List<object> RacersView()
    {
        var list = new List<object>();
        if (LobbyNow)
        {
            for (var i = 0; i < Seats; i++)
                if (Ctx.Seated(i))
                    list.Add(new
                    {
                        seat = i, nick = Ctx.NickOf(i) ?? "", gone = false, c = 0, s = 0, wrong = 0,
                        fin = (long?)null, place = (int?)null, cpm = (int?)null, acc = (int?)null, flag = (string?)null,
                    });
            return list;
        }
        foreach (var r in Racers) if (r.In) list.Add(RacerView(r));
        return list;
    }

    static string? Iso(DateTimeOffset? t) => t?.ToString("O");

    /// <summary>
    /// Спільна частина виду (spec §4.1) — словником, бо соло дописує ще <c>me</c> і <c>noTexts</c>, а за столом
    /// цих полів на дроті нема зовсім. Ключі вже в camelCase: словник серіалізатор не перейменовує.
    /// </summary>
    protected Dictionary<string, object?> BaseView(string lobbyPhase)
    {
        var now = Ctx.Clock.UtcNow;
        var lobby = LobbyNow;
        var showText = !lobby && Phase != PhasePick && Text is not null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["phase"] = lobby ? lobbyPhase : Phase,
            ["turn"] = null,
            ["len"] = showText ? Len : 0,
            ["text"] = showText ? Text : null,
            ["src"] = showText && Src is { } s ? new { kind = s.Kind, author = s.Author, title = s.Title, year = s.Year } : null,
            ["srcNote"] = showText ? SrcNote : null,
            ["opts"] = new { length = Length, source = Source },
            ["readyAt"] = showText ? Iso(ReadyAt) : null,
            ["goAt"] = showText ? Iso(GoAt) : null,
            ["endsAt"] = showText ? Iso(EndsAt) : null,
            // скільки лишилось на мить складання виду: клієнт рахує від миті отримання, і різниця годинників браузера
            // й сервера нічого не псує (ISO-поля — для людей і тестів)
            ["goIn"] = showText && Phase == PhaseReady && ReadyAt is { } ra ? (long?)Math.Max(0L, (long)(ra.AddMilliseconds(ReadyMs) - now).TotalMilliseconds) : null,
            ["endsIn"] = showText && Phase == PhaseGo && EndsAt is { } ea ? (long?)Math.Max(0L, (long)(ea - now).TotalMilliseconds) : null,
            ["tail"] = TailSet && !lobby,
            ["racers"] = RacersView(),
            ["result"] = !lobby && Phase == PhaseDone && Result is { } res
                ? new
                {
                    order = (int[])res.Order.Clone(), winners = (int[])res.Winners.Clone(), say = res.Say, allDone = res.AllDone,
                    trap = res.Trap is { } tr ? new { word = tr.Word, n = tr.N, of = tr.Of } : null,
                }
                : null,
        };
    }
}
