using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Скільки? дня»: ті самі п'ять запитань для всіх за київську добу (сід від дня), одна спроба. Серед них — одне
/// фото «📷 Якого року?», якщо бібліотека готова. Очки — та сама шкала точності, що й за столом (самому — без
/// бонусу найближчому). Щоденний каркас (<see cref="IDailyGame"/>) дає «☀ Сьогодні», серію й щоденний глек, а
/// таблиця дня за очками — своя (<see cref="SkilkyDailyBoard"/>): каркасна рахує «менше — краще».
/// </summary>
public sealed class SkilkyDaily : Skilky, IDailyGame
{
    /// <summary>Скільки запитань дня.</summary>
    public const int DayQuestions = 5;
    const string Puzzle = "skilky";

    public override GameInfo Info { get; } = new(
        "skilky-daily", "Скільки? дня", "«Скільки? дня»", GameGroup.Solo, 1, 1,
        TickMs: 1000, Start: StartMode.Immediate, Private: true, Persistent: true, Hidden: true,
        Hint: "П'ять питань дня — однакові для всіх, одна спроба. Хто сьогодні найточніший? Таблиця дня — у грі",
        Client: "skilky");

    string _day = "";
    /// <summary>Результат уже пішов у таблицю й сервіси — «Ще раз» і відновлення не платять удруге.</summary>
    bool _reported;
    SkilkyDailyBoard? _board;

    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    public override void Start()
    {
        _board ??= Ctx.Services.GetService<SkilkyDailyBoard>();
        var today = Days.Today(Ctx.Clock);
        // День уже зіграно («Ще раз» на дограному дні каркас пускає) — лишаємо підсумок, нову партію не даємо.
        if (_reported && _day == today) { Ctx.Finish([0], ""); return; }
        _day = today;
        _reported = false;
        base.Start();
    }

    /// <summary>
    /// Питання дня: статичні з банку (без динамічних — «скільки треків за тиждень» міняється щогодини, а таблиця
    /// дня має бути чесною), сід від дня. Одне з п'яти — фото, якщо обране днем фото вже докачане.
    /// </summary>
    private protected override List<(SkilkyQuestion Q, double A)> Pick()
    {
        var rng = new Random(Days.Seed(Puzzle, _day));
        var bank = SkilkyBank.All.Where(q => !q.IsDynamic && q.A is not null).OrderBy(q => q.Key, StringComparer.Ordinal).ToList();
        var list = new List<(SkilkyQuestion Q, double A)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        // Фото обираємо з усього маніфесту (а не з готових), щоб день був однаковий у всіх; не докачане — просто без фото.
        SkilkyPhoto? photo = null;
        if (_photos is { All.Count: > 0 } lib)
        {
            var p = lib.All[rng.Next(lib.All.Count)];
            if (lib.Ready(p.Id)) photo = p;
        }
        var need = photo is null ? DayQuestions : DayQuestions - 1;
        for (var guard = 0; list.Count < need && guard < 1000 && bank.Count > 0; guard++)
        {
            var q = bank[rng.Next(bank.Count)];
            if (used.Add(q.Key)) list.Add((q, q.A!.Value));
        }
        if (photo is not null) list.Insert(rng.Next(list.Count + 1), (PhotoQuestion(photo), photo.Year));
        return list;
    }

    /// <summary>Самому за день — ні ставок, ні команд, ні своїх питань: лише число.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload) =>
        action == "answer" ? base.Act(seat, action, payload) : ActResult.Fail("У «Скільки? дня» — лише твоє число");

    public override bool ActsInLobby => false;

    /// <summary>Кінець дня: таблиця дня, щоденний глек, черепки за очки й рядок у Журнал (він же — у Балачки).</summary>
    private protected override void Done()
    {
        _phase = PhaseDone;
        _winners = [0];
        var points = (int)_scores[0];
        if (!_reported)
        {
            _reported = true;
            var nick = Ctx.NickOf(0) ?? "";
            _board?.Record(_day, nick, points, Marks(), Ctx.Clock.UtcNow);
            // Спроба в щоденному каркасі одна; очки там не лягають (він рахує «менше — краще»), тож час — 0.
            Ctx.Score(0, 0, 1);
            Ctx.Award(0, 0, $"daily:{Info.Id}");
            if (Shards(points) is > 0 and var shards) Ctx.Award(0, shards, $"points:{Days.Number(_day).ToString(CultureInfo.InvariantCulture)}");
            Ctx.Finish([0], $"{Info.Title}: {nick} — {points} з {Max} {Plural(points)} {Marks()}");
            return;
        }
        Ctx.Finish([0], "");
    }

    /// <summary>Максимум очок дня: самому — лише за точність.</summary>
    int Max => _asked.Count * Bullseye;

    static string Plural(int n)
    {
        int d = n % 10, h = n % 100;
        return d == 1 && h != 11 ? "очко" : d is >= 2 and <= 4 && (h < 12 || h > 14) ? "очки" : "очок";
    }

    /// <summary>Смужка по запитаннях: 🎯 точно, 🟩 4, 🟨 3, 🟧 2, 🟥 1, ⬛ мимо — без самих чисел, тож ділитись не шкода.</summary>
    string Marks()
    {
        var sb = new StringBuilder();
        foreach (var r in _recap)
            sb.Append(r.Value is null ? "⬛" : r.Points switch { >= Bullseye => "🎯", 4 => "🟩", 3 => "🟨", 2 => "🟧", 1 => "🟥", _ => "⬛" });
        for (var i = _recap.Count; i < _asked.Count; i++) sb.Append("⬛");
        return sb.ToString();
    }

    private protected override object? DailyView(int me)
    {
        var top = _board?.Top(_day) ?? [];
        var nick = Ctx.NickOf(0);
        var mine = nick is null ? -1 : top.ToList().FindIndex(r => SkilkySeen.NickKey(r.Nick) == SkilkySeen.NickKey(nick));
        return new
        {
            day = _day,
            no = _day.Length == 0 ? 0 : Days.Number(_day),
            max = Max,
            // Таблицю дня показуємо лише після своєї спроби: інакше це підказка «хтось набрав 25 — питання легкі».
            board = _phase != PhaseDone ? null : top.Take(10).Select(r => new { nick = r.Nick, points = r.Points, marks = r.Marks }).ToArray(),
            place = _phase != PhaseDone || mine < 0 ? (int?)null : mine + 1,
            players = top.Count,
            share = _phase != PhaseDone ? null : $"Скільки? дня №{Days.Number(_day)} — {(int)_scores[0]}/{Max}\n{Marks()}\nhttps://hlechyky.pp.ua/#games",
        };
    }

    sealed record Saved(string Day, int At, long Score, bool Done, List<SavedRecap>? Recap);
    sealed record SavedRecap(string Q, string? Unit, double Answer, bool Years, double? Value, int Points, bool Photo);
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public override string? Save() => _day.Length == 0 ? null : JsonSerializer.Serialize(new Saved(_day, _at, _scores[0], _reported,
        [.. _recap.Select(r => new SavedRecap(r.Question, r.Unit, r.Answer, r.Years, r.Value, r.Points, r.Photo))]), Wire);

    /// <summary>
    /// Повернувся посеред дня: зіграний день — лише підсумок (удруге не пограєш); недограний — з того самого
    /// запитання, з паузою «готуйсь» (зіграні лишаються зіграними).
    /// </summary>
    public override void Load(string json)
    {
        Saved? saved;
        try { saved = JsonSerializer.Deserialize<Saved>(json, Wire); }
        catch (JsonException) { return; }
        if (saved is null || saved.Day != _day || _asked.Count == 0) return;
        _recap.Clear();
        foreach (var r in saved.Recap ?? []) _recap.Add(new Recap(r.Q, r.Unit, r.Answer, r.Years, r.Value is null ? [] : [0], r.Value, r.Points, -1, r.Photo));
        _scores[0] = Math.Max(0, saved.Score);
        if (saved.Done)
        {
            _reported = true;
            _at = Math.Max(0, _asked.Count - 1);
            _phase = PhaseDone;
            _winners = [0];
            Ctx.Finish([0], "");
            return;
        }
        _at = Math.Clamp(Math.Max(saved.At, _recap.Count), 0, _asked.Count - 1);
        Open(Ctx.Clock.UtcNow);
    }
}
