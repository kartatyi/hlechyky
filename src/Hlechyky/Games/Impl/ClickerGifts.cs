using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Розділ <c>Clicker</c> конфігу (на проді — appsettings.Local.json). Поки що лише подарунки гончарям від майстерні.
/// </summary>
public sealed class ClickerOptions
{
    public List<ClickerGift> Gifts { get; set; } = [];
}

/// <summary>
/// Подарунок гончареві: компенсація за те, що коло само не полагодить (13.10 — серії smaug і Назара, що їх порвали
/// вихід з кола й поганий зв'язок, ClickerLate.cs). Видається раз на <see cref="Id"/>.
/// </summary>
public sealed class ClickerGift
{
    /// <summary>Унікальний ключ: той самий подарунок удруге не видається (і черепки йдуть із ним як ref леджера).</summary>
    public string Id { get; set; } = "";
    /// <summary>Кому — нік акаунта. Регістр не має значення.</summary>
    public string Nick { get; set; } = "";
    /// <summary>Скільки додати до серії глеків з полиці.</summary>
    public int Streak { get; set; }
    /// <summary>Скільки черепків у гаманець — нагородою, без денної стелі.</summary>
    public int Shards { get; set; }
    /// <summary>Короткий рядок у тост, коли листа нема. Порожньо — «🎁 Подарунок від майстерні».</summary>
    public string Note { get; set; } = "";
    /// <summary>Заголовок листа.</summary>
    public string Title { get; set; } = "";
    /// <summary>
    /// Лист від Дядька Глека: вікно в колі з кнопкою «Дякую», доки гончар його не закриє (з будь-якого пристрою). Абзаци —
    /// через порожній рядок. Порожньо — листа нема, лише тост.
    /// </summary>
    public string Letter { get; set; } = "";
}

public sealed partial class Clicker
{
    /// <summary>Причина в леджері: категорія «нагороди» в гаманці.</summary>
    public const string GiftReason = "award:🎁 подарунок від Гончарного кола";

    /// <summary>Id виданих подарунків. Зберігається: подарунок не повториться ні після перезапуску, ні з іншого пристрою.</summary>
    readonly HashSet<string> _takenGifts = new(StringComparer.Ordinal);
    /// <summary>Id подарунків, чий лист гончар ще не закрив. Зберігається: лист дочекається його й на іншому пристрої.</summary>
    readonly HashSet<string> _letters = new(StringComparer.Ordinal);

    List<ClickerGift> Gifts => Ctx.Services.GetService<IOptionsMonitor<ClickerOptions>>()?.CurrentValue.Gifts ?? [];

    /// <summary>
    /// Видати подарунки, що чекають цього гончаря (з конфігу, <see cref="ClickerOptions.Gifts"/>). Кличеться після
    /// вдалої справжньої дії: гончар тут і побачить. Повертає рядок для тосту (подарунки без листа) або порожньо.
    /// </summary>
    string TakeGifts()
    {
        var gifts = Gifts;
        if (gifts.Count == 0 || Ctx.NickOf(0) is not { Length: > 0 } nick) return "";
        var said = new List<string>();
        foreach (var gift in gifts)
        {
            if (gift.Id.Length == 0 || !string.Equals(gift.Nick.Trim(), nick, StringComparison.OrdinalIgnoreCase)
                || _takenGifts.Contains(gift.Id))
                continue;
            _takenGifts.Add(gift.Id);
            if (gift.Streak > 0) _fallStreak += gift.Streak;
            if (gift.Shards > 0 && Ctx.Services.GetService<IStakes>() is { } stakes)
                stakes.Grant(nick, gift.Shards, GiftReason, $"clicker-gift:{gift.Id}");
            // З листом — вікно в колі (вид, letter); без нього — тост.
            if (gift.Letter.Trim().Length > 0) { _letters.Add(gift.Id); continue; }
            var note = gift.Note.Trim().Length > 0 ? gift.Note.Trim() : "🎁 Подарунок від майстерні";
            var what = GiftWhat(gift);
            said.Add(what.Length > 0 ? $"{note}: {what}" : note);
        }
        return string.Join(" · ", said);
    }

    static string GiftWhat(ClickerGift gift) => string.Join(" · ",
        new[] { gift.Streak > 0 ? $"+{gift.Streak} до серії" : "", gift.Shards > 0 ? $"+{gift.Shards} черепків" : "" }
            .Where(s => s.Length > 0));

    /// <summary>Перший непрочитаний лист — у вид. Запис з конфігу прибрали — і листа нема.</summary>
    object? LetterView()
    {
        if (_letters.Count == 0) return null;
        foreach (var gift in Gifts)
            if (_letters.Contains(gift.Id) && gift.Letter.Trim().Length > 0)
                return new
                {
                    id = gift.Id,
                    title = gift.Title.Trim().Length > 0 ? gift.Title.Trim() : "Лист від Дядька Глека",
                    text = gift.Letter.Trim(),
                    gift = GiftWhat(gift),
                };
        return null;
    }

    /// <summary>Гончар закрив лист (<c>letter {id}</c>) — більше його не показуємо.</summary>
    ActResult SeenLetter(JsonElement payload)
    {
        _letters.Remove(Str(payload, "id"));
        return ActResult.Done;
    }
}
