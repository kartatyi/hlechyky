using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Прокльон, дзвінок і святкування (docs/games/specs/flair.md): наслати, відкупитись, дізнатися від кого; коли прокльон
/// спрацьовує за столом (<see cref="AnthemPlayer"/>); «Свій дзвінок» в особистому заклику (<see cref="Calls"/>) і на дроті;
/// святкування перемоги в події <c>anthem</c>; форма вітрини й HTTP. Справжня економіка (EconomyRig), розсилка Лавки —
/// заглушка. Жодного звуку: файли лише перевіряються на диску.
/// </summary>
public sealed class FlairTests : IDisposable
{
    // =============================================================================================
    // Заглушки й рига
    // =============================================================================================

    sealed class NoAir : ILavkaAir
    {
        public IReadOnlyList<LavkaQueued> Queue() => [];
        public (bool Ok, string Message) AddVoiceBefore(TrackInfo track, string filePath, string nick, string beforeItemId) => (false, "");
    }

    sealed class NoVoice : ILavkaVoice
    {
        public Task<LavkaVoiceClip?> SpeakAsync(string text, string title, string artist, CancellationToken ct) => Task.FromResult<LavkaVoiceClip?>(null);
    }

    sealed class FakeWire : ILavkaWire
    {
        readonly object _lock = new();
        public List<(string Nick, LavkaLook? Look)> Shown { get; } = [];
        public List<(string Nick, string Text)> Toasts { get; } = [];
        public List<string> Journal { get; } = [];
        void ILavkaWire.Look(string nick, LavkaLook? look) { lock (_lock) Shown.Add((nick, look)); }
        void ILavkaWire.Fireworks(string nick) { }
        void ILavkaWire.Chat(object line) { }
        void ILavkaWire.Toast(string nick, string text) { lock (_lock) Toasts.Add((nick, text)); }
        void ILavkaWire.Journal(string text) { lock (_lock) Journal.Add(text); }
    }

    sealed class BrokenRings : IRings
    {
        public InviteRing? RingOf(string nick) => throw new InvalidOperationException("база Лавки лягла");
    }

    readonly EconomyRig _eco = new();
    readonly FakeWire _wire = new();
    readonly LavkaStore _store;
    readonly Lavka _lavka;
    readonly AnthemPlayer _player;

    FakeClock Clock => _eco.Clock;
    List<Anthem> Played => _eco.Outbox.Of<Anthem>();
    List<Curse> Cursed => _eco.Outbox.Of<Curse>();

    public FlairTests()
    {
        foreach (var nick in new[] { "Оля", "Петро", "Андрій", "Марта", "Ганна", "Тест Оля" })
            Assert.True(_eco.Db.AddAccount(nick, "", "salt"));
        _store = new LavkaStore(_eco.Db);
        _lavka = new Lavka(_store, _eco.Economy, _eco.Store, _eco.Db, _eco.Presence, new NoAir(), new NoVoice(), _wire, Clock,
            NullLogger<Lavka>.Instance);
        _player = new AnthemPlayer(_eco.Events, _lavka, _eco.Outbox, NullLogger<AnthemPlayer>.Instance);
        _player.StartAsync(default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _player.StopAsync(default).GetAwaiter().GetResult();
        _eco.Dispose();
    }

    void Give(string nick, int shards) => _eco.Economy.Grant(nick, shards, "listen", "test:" + Guid.NewGuid().ToString("N"));

    int Balance(string nick) => _eco.Economy.Balance(nick);

    LavkaReply Buy(string nick, string item, string? to = null) => _lavka.Buy(nick, account: true, item, to);

    /// <summary>Наслати прокльон за свої черепки (гроші дає тест) і повернути id рядка.</summary>
    long Hex(string from, string to, string item = "goat")
    {
        Give(from, LavkaCatalog.CursePrice);
        var r = _lavka.Curse(from, true, to, item);
        Assert.True(r.Ok, r.Message);
        return _store.LiveOn(to).Single(c => c.FromKey == Auth.NickKey(from)).Id;
    }

    void Wear(string nick, string item)
    {
        Give(nick, LavkaCatalog.Get(item)!.Price);
        var r = Buy(nick, item);
        Assert.True(r.Ok, r.Message);
    }

    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нулики", "хрестики-нулики");
    static readonly GameInfo Mafia = EconomyRig.Info("mafia", "Мафія", "мафію", GameGroup.Party, max: 10);

    /// <summary>Запит від імені ніка: акаунт — з рядком у c.Items, як його кладе UseHlechykyAuth.</summary>
    static Microsoft.AspNetCore.Http.HttpContext As(string nick, bool account = true)
    {
        var c = Radio.As(nick);
        if (account) c.Items["account"] = new Account(nick, "", "salt", "member");
        return c;
    }

    // =============================================================================================
    // Каталог
    // =============================================================================================

    static readonly string[] CurseIds = ["sadtrombone", "boo", "crickets", "funeral", "goat", "clown"];

    [Fact]
    public void Curse_shelf_has_six_curses_at_500_with_a_sound_each()
    {
        var curses = LavkaCatalog.All.Where(i => i.Kind == LavkaKind.Curse).ToList();
        Assert.Equal(CurseIds, curses.Select(i => i.Id));
        Assert.All(curses, i => Assert.Equal(500, i.Price));
        Assert.Equal(new LavkaAnthemArt("🐐", "/static/curses/goat.mp3"), LavkaCatalog.Get("goat")!.Art);
        // сумний тромбон — той самий файл, що й гімн
        Assert.Equal(((LavkaAnthemArt)LavkaCatalog.Get("trombone")!.Art).Url, ((LavkaAnthemArt)LavkaCatalog.Get("sadtrombone")!.Art).Url);
        Assert.Equal("прокльон «Цап»", LavkaCatalog.Label("goat"));
        Assert.DoesNotContain(LavkaKind.Curse, LavkaCatalog.Slots);
    }

    [Fact]
    public void Every_curse_has_its_mp3_in_web_static()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "static"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        foreach (var id in CurseIds)
        {
            var url = ((LavkaAnthemArt)LavkaCatalog.Get(id)!.Art).Url!;
            if (id != "sadtrombone") Assert.Equal($"/static/curses/{id}.mp3", url);
            var file = new FileInfo(Path.Combine(dir!, "web", url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(file.Exists, $"нема web{url} — прокльон грав би тишу (docs/games/dev/curses-make.py)");
            Assert.True(file.Length > 0, $"порожній web{url}");
        }
    }

    [Fact]
    public void Fx_shelf_and_the_ring_perk_have_their_prices()
    {
        var fx = LavkaCatalog.All.Where(i => i.Kind == LavkaKind.Fx).ToList();
        Assert.Equal(["confetti", "shards", "sunflowers", "salute", "glekhopak"], fx.Select(i => i.Id));
        Assert.Equal([400, 600, 600, 1000, 1500], fx.Select(i => i.Price));
        Assert.Equal(new LavkaFxArt("💃"), LavkaCatalog.Get("glekhopak")!.Art);
        Assert.Contains(LavkaKind.Fx, LavkaCatalog.Slots);
        Assert.Equal("святкування «Конфеті»", LavkaCatalog.Label("confetti"));

        var ring = LavkaCatalog.Get(LavkaCatalog.Ring)!;
        Assert.Equal((LavkaKind.Perk, "Свій дзвінок", 500), (ring.Kind, ring.Title, ring.Price));
    }

    // =============================================================================================
    // Наслати
    // =============================================================================================

    [Fact]
    public void Cursing_costs_500_with_a_ref_tells_the_target_and_the_journal_but_never_who()
    {
        Give("Оля", 1000);
        var before = Balance("Оля");
        var r = _lavka.Curse("Оля", true, "петро", "goat");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before - 500, r.Balance);
        Assert.Equal(before - 500, Balance("Оля"));

        var row = Assert.Single(_store.LiveOn("Петро"));
        Assert.Equal(("Оля", "Петро", "goat", 3, 500), (row.FromNick, row.ToNick, row.Item, row.Left, row.Price));
        var move = Assert.Single(_eco.Economy.Moves("curse:")!);
        Assert.Equal(($"curse:{row.Id}", -500), (move.Ref, move.Delta));
        Assert.Contains(_eco.Outbox.Of<WalletChanged>(), w => w.Reason == "curse:goat" && w.Text == "−500 черепків: Лавка — прокльон «Цап» · ціль: Петро");
        Assert.Equal("Лавка — прокльон «Цап»", _eco.Economy.Reason("curse:goat"));
        Assert.Equal("Лавка — відкуп від прокльону", _eco.Economy.Reason("curse-ransom:goat"));
        Assert.Equal("Лавка — хто наслав прокльон", _eco.Economy.Reason("curse-reveal:goat"));

        var toast = Assert.Single(_wire.Toasts);
        Assert.Equal(("Петро", "😈 На тебе наклали прокльон «Цап» на 3 програші. Від кого — секрет. Відкупитись — у Лавці"), toast);
        Assert.Equal(["😈 Петро тепер під прокльоном. Хто наслав — секрет"], _wire.Journal);
        Assert.DoesNotContain(_wire.Journal, j => j.Contains("Оля"));
        Assert.Equal("😈 Прокльон «Цап» наслано. Ціль: Петро — почує його на трьох своїх програшах. Від кого — секрет", r.Message);

        // нік із кількох слів — лише в називному: відмінок зламав би його («на Теста Олю»)
        Give("Петро", 500);
        r = _lavka.Curse("Петро", true, "тест оля", "boo");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("😈 Тест Оля тепер під прокльоном. Хто наслав — секрет", _wire.Journal[^1]);
        Assert.Equal("😈 Прокльон «Бу-у-у» наслано. Ціль: Тест Оля — почує його на трьох своїх програшах. Від кого — секрет", r.Message);
        Assert.Contains(_eco.Outbox.Of<WalletChanged>(), w => w.Text == "−500 черепків: Лавка — прокльон «Бу-у-у» · ціль: Тест Оля");
    }

    [Fact]
    public void Guests_non_accounts_self_and_the_normal_shop_are_refused()
    {
        Give("Оля", 2000);
        var before = Balance("Оля");
        Assert.Equal(Lavka.NotAccount, _lavka.Curse("гість Вася", false, "Петро", "goat").Message);
        Assert.Equal(Lavka.CurseToGuest, _lavka.Curse("Оля", true, "гість Вася", "goat").Message);
        Assert.Equal(Lavka.CurseToGuest, _lavka.Curse("Оля", true, "Незнайомець", "goat").Message);
        Assert.Equal(Lavka.CurseSelf, _lavka.Curse("Оля", true, "оля", "goat").Message);
        Assert.Equal("Кого проклясти?", _lavka.Curse("Оля", true, "  ", "goat").Message);
        Assert.Equal("Такого прокльону в Лавці нема", _lavka.Curse("Оля", true, "Петро", "fox").Message);
        // купити чи подарувати прокльон звичайним шляхом не можна
        Assert.Equal(Lavka.CurseNotSold, Buy("Оля", "goat").Message);
        Assert.Equal(Lavka.CurseNotSold, Buy("Оля", "goat", to: "Петро").Message);
        Assert.Equal(Lavka.NoSlot, _lavka.Wear("Оля", true, "curse", "goat").Message);
        Assert.Equal(before, Balance("Оля"));
        Assert.Empty(_store.LiveOn("Петро"));
        Assert.Empty(_wire.Toasts);

        // бракує — ні рядка, ні грошей
        Assert.StartsWith("Бракує", _lavka.Curse("Марта", true, "Петро", "goat").Message);
        Assert.Empty(_store.LiveOn("Петро"));
    }

    [Fact]
    public void One_live_curse_per_pair_and_three_per_target()
    {
        Hex("Оля", "Петро");
        Give("Оля", 500);
        var again = _lavka.Curse("Оля", true, "Петро", "clown");
        Assert.False(again.Ok);
        Assert.Equal("Твій прокльон на цю людину ще діє — хай спершу розрядиться", again.Message);

        Hex("Андрій", "Петро", "boo");
        Hex("Марта", "Петро", "clown");
        Give("Ганна", 500);
        var before = Balance("Ганна");
        var fourth = _lavka.Curse("Ганна", true, "Петро", "funeral");
        Assert.False(fourth.Ok);
        Assert.Equal("На цій людині вже три прокльони — більше не влізе", fourth.Message);
        Assert.Equal(before, Balance("Ганна"));
        Assert.Equal(3, _store.LiveOn("Петро").Count);

        // на іншу ціль — будь ласка
        Assert.True(_lavka.Curse("Ганна", true, "Оля", "funeral").Ok);
    }

    // =============================================================================================
    // Коли спрацьовує
    // =============================================================================================

    [Fact]
    public void Losing_to_a_human_plays_the_curse_counts_down_and_tells_the_curser()
    {
        var id = Hex("Оля", "Петро");
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: 4));

        Assert.Equal(new Curse("r1", 4, "Петро", "Цап", "🐐", "/static/curses/goat.mp3", 2), Assert.Single(Cursed));
        Assert.Empty(Played);                                                    // у переможниці гімну нема
        Assert.Equal(2, _store.Curse(id)!.Left);
        Assert.Contains(("Оля", "😈 Твій прокльон «Цап» спрацював (лишилось 2). Ціль: Петро"), _wire.Toasts);
    }

    [Fact]
    public void Losing_to_a_bot_alone_draws_coop_and_solo_do_nothing()
    {
        var id = Hex("Оля", "Петро");
        var solo = EconomyRig.Info("snake", "Змійка", "змійку", GameGroup.Solo, max: 1);
        _eco.Events.Raise(_eco.Finished("b1", Ttt, ["Петро", null], [1]));            // програв ботові на самоті
        _eco.Events.Raise(_eco.Finished("b2", Ttt, ["Петро", "петро"], [1]));         // сам із собою на двох місцях
        _eco.Events.Raise(_eco.Finished("d1", Ttt, ["Оля", "Петро"], [0, 1], draw: true));
        _eco.Events.Raise(_eco.Finished("c1", Ttt with { Id = "coop", Coop = true }, ["Оля", "Петро"], [0]));
        _eco.Events.Raise(_eco.Finished("s1", solo, ["Петро"], [0]));
        _eco.Events.Raise(_eco.Finished("e1", Ttt, ["Оля", "Петро"], []));
        Assert.Empty(Cursed);
        Assert.Equal(3, _store.Curse(id)!.Left);

        // бот виграв, але за столом двоє людей — прокльон спрацьовує
        _eco.Events.Raise(_eco.Finished("b3", Mafia, ["Петро", null, "Андрій"], [1]));
        Assert.Equal("Петро", Assert.Single(Cursed).Nick);
    }

    [Fact]
    public void A_cursed_winner_hears_nothing()
    {
        var id = Hex("Оля", "Петро");
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", "Петро"], [1]));
        Assert.Empty(Cursed);
        Assert.Equal(3, _store.Curse(id)!.Left);
    }

    [Fact]
    public void Several_cursed_losers_take_turns_by_round_in_seat_order()
    {
        Hex("Оля", "Петро");
        Hex("Оля", "Андрій", "clown");
        for (var round = 1; round <= 4; round++)
            _eco.Events.Raise(_eco.Finished("m1", Mafia, ["Оля", "Петро", null, "Андрій", "гість Вася"], [0], round: round));

        Assert.Equal(["Андрій", "Петро", "Андрій", "Петро"], Cursed.Select(c => c.Nick));   // кандидати[Round % 2]
        Assert.Equal([2, 2, 1, 1], Cursed.Select(c => c.Left));
        Assert.Equal("Клоун", Cursed[0].Title);
    }

    [Fact]
    public void The_same_room_and_round_curses_once_and_one_finish_gives_one_anthem_and_one_curse()
    {
        Wear("Оля", "trembita");
        Hex("Андрій", "Петро");
        Hex("Марта", "Петро", "boo");
        var e = _eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: 7);
        _eco.Events.Raise(e);
        _player.On(e);
        _eco.Events.Raise(e with { FinishedAt = e.FinishedAt.AddSeconds(1) });

        Assert.Single(Played);
        var curse = Assert.Single(Cursed);                                     // два прокльони на Петрові — грає один
        Assert.Equal("Цап", curse.Title);                                       // найстаріший (FIFO)
        var all = _eco.Outbox.All.Where(m => m is Anthem or Curse).ToList();
        Assert.IsType<Anthem>(all[0]);                                         // гімн — першим, прокльон — за ним
        Assert.IsType<Curse>(all[1]);
    }

    [Fact]
    public void Three_losses_discharge_the_curse()
    {
        var id = Hex("Оля", "Петро");
        for (var round = 1; round <= 4; round++)
            _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: round));

        Assert.Equal([2, 1, 0], Cursed.Select(c => c.Left));
        var row = _store.Curse(id)!;
        Assert.Equal((0, "done"), (row.Left, row.State));
        Assert.NotNull(row.DoneAt);
        Assert.Contains(("Оля", "😈 Твій прокльон «Цап» спрацював і розрядився. Ціль: Петро"), _wire.Toasts);
        Assert.False(_lavka.Cursed("Петро"));

        // розрядився — і можна наслати знову
        Give("Оля", 500);
        Assert.True(_lavka.Curse("Оля", true, "Петро", "boo").Ok);
    }

    // =============================================================================================
    // Відкуп і розкриття
    // =============================================================================================

    [Fact]
    public void Ransom_burns_1000_lifts_the_curse_and_tells_the_curser()
    {
        var id = Hex("Оля", "Петро");
        var olya = Balance("Оля");
        Assert.Equal(Lavka.NotYourCurse, _lavka.Ransom("Андрій", true, id).Message);   // чужий
        Assert.Equal(Lavka.NotYourCurse, _lavka.Ransom("Петро", true, 999).Message);
        Assert.Equal(Lavka.NotYourCurse, _lavka.Ransom("Петро", true, null).Message);
        Assert.StartsWith("Бракує", _lavka.Ransom("Петро", true, id).Message);
        Assert.Equal(Lavka.NotAccount, _lavka.Ransom("Петро", false, id).Message);

        Give("Петро", 1000);
        var before = Balance("Петро");
        var r = _lavka.Ransom("Петро", true, id);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before - 1000, Balance("Петро"));
        Assert.Equal(olya, Balance("Оля"));                                     // тому, хто наслав, — нічого
        Assert.Equal(-1000, _eco.Paid("Петро", "curse-ransom:goat"));
        var move = Assert.Single(_eco.Economy.Moves("curse-ransom:")!);
        Assert.Equal(($"curse-ransom:{id}", -1000), (move.Ref, move.Delta));
        var row = _store.Curse(id)!;
        Assert.Equal((0, "ransomed"), (row.Left, row.State));
        Assert.Contains(("Оля", "😇 Від твого прокльону «Цап» відкупились. Ціль: Петро"), _wire.Toasts);

        Give("Петро", 1000);
        Assert.Equal(Lavka.CurseGone, _lavka.Ransom("Петро", true, id).Message);   // повтор
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", "Петро"], [0]));
        Assert.Empty(Cursed);
    }

    [Fact]
    public void Reveal_costs_300_shows_the_author_once_and_only_for_a_week()
    {
        var id = Hex("Оля", "Петро");
        Give("Петро", 1000);
        Assert.Equal(Lavka.NotYourCurse, _lavka.Reveal("Андрій", true, id).Message);
        var onMe = Views.Json(_lavka.View("Петро", true)).GetProperty("curses").GetProperty("onMe")[0];
        Assert.False(onMe.TryGetProperty("from", out _));                       // ще не розкрито — поля нема

        var before = Balance("Петро");
        var r = _lavka.Reveal("Петро", true, id);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("🕵️ Хто наслав «Цап»: Оля", r.Message);
        Assert.Equal(before - 300, Balance("Петро"));
        Assert.Equal(-300, _eco.Paid("Петро", "curse-reveal:goat"));
        Assert.Contains(("Оля", "🕵️ Твій прокльон «Цап» розкрито: Петро знає, що він від тебе"), _wire.Toasts);
        Assert.Equal("Ти вже знаєш — це Оля", _lavka.Reveal("Петро", true, id).Message);   // повтор
        Assert.Equal(before - 300, Balance("Петро"));

        var view = Views.Json(_lavka.View("Петро", true)).GetProperty("curses");
        Assert.Equal("Оля", view.GetProperty("onMe")[0].GetProperty("from").GetString());
        Assert.True(Views.Json(_lavka.View("Оля", true)).GetProperty("curses").GetProperty("mine")[0].GetProperty("revealed").GetBoolean());

        // розряджений можна розкрити ще 7 днів, далі його не видно й не розкрити
        Give("Петро", 1000);
        Assert.True(_lavka.Ransom("Петро", true, id).Ok);                      // перший знято — програші йдуть другому
        var other = Hex("Андрій", "Петро", "boo");
        for (var round = 1; round <= 3; round++)
            _eco.Events.Raise(_eco.Finished("r" + round, Ttt, ["Андрій", "Петро", "Оля"], [0], round: round));
        Assert.Equal("done", _store.Curse(other)!.State);
        Clock.Advance(TimeSpan.FromDays(6));
        Assert.Contains(Views.Json(_lavka.View("Петро", true)).GetProperty("curses").GetProperty("onMe").EnumerateArray(),
            c => c.GetProperty("id").GetInt64() == other);
        Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(Lavka.CurseGone, _lavka.Reveal("Петро", true, other).Message);
        Assert.DoesNotContain(Views.Json(_lavka.View("Петро", true)).GetProperty("curses").GetProperty("onMe").EnumerateArray(),
            c => c.GetProperty("id").GetInt64() == other);
    }

    // =============================================================================================
    // Свій дзвінок
    // =============================================================================================

    [Fact]
    public void Ring_needs_the_perk_and_an_own_playable_anthem()
    {
        Assert.Equal(Lavka.NoRing, _lavka.SetRing("Оля", true, "trembita").Message);
        Assert.Equal(Lavka.NotAccount, _lavka.SetRing("гість Вася", false, "trembita").Message);
        Give("Оля", 5000);
        Assert.Equal(Lavka.NoGiftRing, Buy("Оля", LavkaCatalog.Ring, to: "Петро").Message);
        Assert.True(Buy("Оля", LavkaCatalog.Ring).Ok);
        Assert.Null(_store.Worn("Оля", LavkaCatalog.RingSlot));                // куплене вміння ще нічого не обрало
        Assert.Null(_lavka.RingOf("Оля"));

        Assert.Equal("Дзвінком стає лише свій гімн — спершу купи його", _lavka.SetRing("Оля", true, "trembita").Message);
        Assert.Equal("Дзвінком стає лише гімн", _lavka.SetRing("Оля", true, "fox").Message);
        Assert.Equal(Lavka.NoItem, _lavka.SetRing("Оля", true, "nope").Message);
        Assert.True(Buy("Оля", LavkaCatalog.OwnAnthem).Ok);
        Assert.Equal("Спершу постав уривок «Свого треку»", _lavka.SetRing("Оля", true, LavkaCatalog.OwnAnthem).Message);

        Assert.True(Buy("Оля", "trembita").Ok);
        var r = _lavka.SetRing("Оля", true, "trembita");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new AnthemPlay("Трембіта", "📯", "/static/anthems/trembita.mp3"), _lavka.RingOf("Оля")! with { Len = null });
        Assert.Equal("trembita", Views.Json(_lavka.View("Оля", true)).GetProperty("worn").GetProperty("ring").GetString());
        // дзвінок і гімн — окремо: вдягнути інший гімн дзвінка не міняє
        Assert.True(_lavka.Wear("Оля", true, "anthem", LavkaCatalog.OwnAnthem).Ok);
        Assert.Equal("Трембіта", _lavka.RingOf("Оля")!.Title);

        Assert.True(_lavka.SetRing("Оля", true, "").Ok);
        Assert.Null(_lavka.RingOf("Оля"));
        Assert.Equal(JsonValueKind.Null, Views.Json(_lavka.View("Оля", true)).GetProperty("worn").GetProperty("ring").ValueKind);
    }

    sealed class CallRig
    {
        public Rooms Rooms { get; }
        public Calls Calls { get; }
        public Presence Presence { get; } = new();

        public CallRig(FakeClock clock, IRings? rings, params string[] online)
        {
            Rooms = new Rooms(RoomHarness.NewRegistry(), clock, new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty())
            {
                SeedOverride = 7,
            };
            Calls = new Calls(Rooms, Presence, clock, rings);
            foreach (var nick in online) Presence.Set("c-" + nick, nick);
        }

        public string Table(string by) => Rooms.Create(by, "t-party", null).Reply.RoomId!;
    }

    [Fact]
    public void A_personal_call_carries_the_ring_and_a_call_to_everyone_never_does()
    {
        Wear("Оля", LavkaCatalog.Ring);
        Wear("Оля", "trembita");
        Assert.True(_lavka.SetRing("Оля", true, "trembita").Ok);

        var s = new CallRig(Clock, _lavka, "Оля", "Петро", "Андрій");
        var id = s.Table("Оля");
        var call = s.Calls.Invite("Оля", id, "Петро");
        Assert.True(call.Reply.Ok, call.Reply.Message);
        var invite = Assert.Single(call.Out.OfType<Invite>());
        Assert.Equal(new InviteRing("/static/anthems/trembita.mp3", "Трембіта", "📯"), invite.Ring);

        // /клич — той самий особистий заклик
        var cmd = s.Calls.Command("Оля", "Андрій");
        Assert.NotNull(cmd.Out);
        Assert.Equal("Трембіта", Assert.Single(cmd.Out!.OfType<Invite>()).Ring!.Title);

        Clock.Advance(Calls.AgainGap);
        var again = s.Calls.Again("Оля", id);
        Assert.Null(Assert.Single(again.Out.OfType<Invite>()).Ring);

        // без дзвінка в того, хто кличе, — поля нема; зламана Лавка — заклик однаково летить
        var plain = new CallRig(Clock, _lavka, "Петро", "Оля");
        var plainCall = plain.Calls.Invite("Петро", plain.Table("Петро"), "Оля");
        Assert.True(plainCall.Reply.Ok, plainCall.Reply.Message);
        Assert.Null(Assert.Single(plainCall.Out.OfType<Invite>()).Ring);
        var broken = new CallRig(Clock, new BrokenRings(), "Оля", "Петро");
        var brokenCall = broken.Calls.Invite("Оля", broken.Table("Оля"), "Петро");
        Assert.True(brokenCall.Reply.Ok);
        Assert.Null(Assert.Single(brokenCall.Out.OfType<Invite>()).Ring);
    }

    [Fact]
    public void Broadcaster_puts_the_ring_only_into_a_personal_invite()
    {
        var conns = new Dictionary<string, IReadOnlyList<string>> { ["Петро"] = ["c3"] };
        List<Send> Plan(Outgoing m) => Broadcaster.Plan([m], () => [], () => [], _ => null, _ => null,
            n => conns.GetValueOrDefault(n) ?? [], (_, _) => new object());

        var ring = new InviteRing("/static/anthems/trembita.mp3", "Трембіта", "📯");
        var personal = Views.Json(Assert.Single(Plan(new Invite("r1", "Оля", "Оля кличе тебе в шашки", "Петро", ring))).Payload);
        Assert.Equal(["roomId", "by", "text", "personal", "ring"], personal.EnumerateObject().Select(p => p.Name));
        var r = personal.GetProperty("ring");
        Assert.Equal(["url", "title", "emoji"], r.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("/static/anthems/trembita.mp3", "Трембіта", "📯"),
            (r.GetProperty("url").GetString(), r.GetProperty("title").GetString(), r.GetProperty("emoji").GetString()));

        var plain = Views.Json(Assert.Single(Plan(new Invite("r1", "Оля", "Оля кличе тебе в шашки", "Петро"))).Payload);
        Assert.Equal(["roomId", "by", "text", "personal"], plain.EnumerateObject().Select(p => p.Name));
        // загальний заклик дзвінка не несе, навіть якби його туди поклали
        var everyone = Views.Json(Assert.Single(Plan(new Invite("r1", "Оля", "Оля кличе в шашки", null, ring))).Payload);
        Assert.False(everyone.TryGetProperty("ring", out _));
    }

    // =============================================================================================
    // Святкування
    // =============================================================================================

    [Fact]
    public void Fx_is_bought_worn_gifted_like_an_anthem_and_never_drawn_in_the_look()
    {
        Give("Оля", 5000);
        var r = Buy("Оля", "confetti");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Святкування «Конфеті» тепер твоє назавжди — злетить над столом, коли виграєш", r.Message);
        Assert.Equal("confetti", _store.Worn("Оля", LavkaKind.Fx));
        Assert.Equal("confetti", _lavka.FxOf("Оля"));
        Assert.Contains(_eco.Outbox.Of<WalletChanged>(), w => w.Text == "−400 черепків: Лавка — святкування «Конфеті»");
        Assert.Null(_lavka.LookOf("Оля"));                                       // у вигляді святкування нема

        _wire.Shown.Clear();
        Assert.True(_lavka.Wear("Оля", true, "fx", null).Ok);
        Assert.Null(_lavka.FxOf("Оля"));
        Assert.True(_lavka.Wear("Оля", true, "fx", "confetti").Ok);
        Assert.Empty(_wire.Shown);                                              // вдягання святкування нікому не летить
        Assert.Equal(Lavka.NotOwned, _lavka.Wear("Оля", true, "fx", "salute").Message);
        Assert.Equal(Lavka.WrongSlot, _lavka.Wear("Оля", true, "fx", "trembita").Message);

        var gift = Buy("Оля", "glekhopak", to: "Петро");
        Assert.True(gift.Ok, gift.Message);
        Assert.Equal("glekhopak", _lavka.FxOf("Петро"));
        Assert.Contains(_wire.Toasts, t => t.Nick == "Петро" && t.Text.Contains("святкування «Глек танцює гопак»"));
        Assert.Equal("confetti", Views.Json(_lavka.View("Оля", true)).GetProperty("worn").GetProperty("fx").GetString());
    }

    [Fact]
    public void Winner_with_only_fx_gets_an_anthem_event_without_sound_and_with_both_gets_both()
    {
        Wear("Оля", "confetti");
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: 1));
        Assert.Equal(new Anthem("r1", 1, "Оля", null, null, null, "confetti"), Assert.Single(Played));

        var body = Views.Json(Assert.Single(Broadcaster.Plan([Played[0]], () => [], () => [], _ => null, _ => null, _ => [],
            (_, _) => new object())).Payload);
        Assert.Equal(["id", "round", "nick", "title", "emoji", "url", "fx", "len"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("url").ValueKind);
        Assert.Equal("confetti", body.GetProperty("fx").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("len").ValueKind);   // без гімну — браузер бере свої 6 с

        Wear("Оля", "bells");
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: 2));
        // len — довжина файла гімну (кадри mp3), щоб святкування в кожній вкладці тривало стільки ж
        var len = LavkaCatalog.ClipSeconds("/static/anthems/bells.mp3");
        Assert.InRange(len ?? 0, 1, 15);
        Assert.Equal(new Anthem("r1", 2, "Оля", "Дзвони", "🔔", "/static/anthems/bells.mp3", "confetti", len), Played[1]);
        var withLen = Views.Json(Assert.Single(Broadcaster.Plan([Played[1]], () => [], () => [], _ => null, _ => null, _ => [],
            (_, _) => new object())).Payload);
        Assert.Equal(len, withLen.GetProperty("len").GetDouble());
    }

    [Fact]
    public void Winners_with_fx_join_the_anthem_rotation()
    {
        Wear("Оля", "trembita");
        Wear("Андрій", "salute");
        for (var round = 1; round <= 2; round++)
            _eco.Events.Raise(_eco.Finished("m1", Mafia, ["Оля", "Петро", "Андрій"], [0, 2], round: round));
        Assert.Equal(["Андрій", "Оля"], Played.Select(a => a.Nick));
        Assert.Equal(("salute", (string?)null), (Played[0].Fx, Played[0].Url));
        Assert.Equal(((string?)null, "Трембіта"), (Played[1].Fx, Played[1].Title));
    }

    // =============================================================================================
    // Розсилка прокльону
    // =============================================================================================

    [Fact]
    public void Broadcaster_sends_curse_to_the_table_group_and_the_seated_elsewhere()
    {
        var b = new RoomBroadcast("r7", null!, false, ["Оля", "Петро"], new Dictionary<int, object?>(), null, ["c1"]);
        var conns = new Dictionary<string, IReadOnlyList<string>> { ["Оля"] = ["c1"], ["Петро"] = ["c3"] };
        var sends = Broadcaster.Plan([new Curse("r7", 3, "Петро", "Цап", "🐐", "/static/curses/goat.mp3", 2)],
            () => [], () => [], id => id == "r7" ? b : null, _ => null, n => conns.GetValueOrDefault(n) ?? [], (_, _) => new object());

        Assert.Equal(2, sends.Count);
        Assert.Equal(new ToGroup("room:r7"), sends[0].Target);
        Assert.Equal(["c3"], Assert.IsType<ToConnections>(sends[1].Target).Ids);
        Assert.All(sends, s => Assert.Equal("curse", s.Event));
        var body = Views.Json(sends[0].Payload);
        Assert.Equal(["id", "round", "nick", "title", "emoji", "url", "left"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("r7", 3, "Петро", "Цап", "🐐", "/static/curses/goat.mp3", 2),
            (body.GetProperty("id").GetString(), body.GetProperty("round").GetInt32(), body.GetProperty("nick").GetString(),
                body.GetProperty("title").GetString(), body.GetProperty("emoji").GetString(), body.GetProperty("url").GetString(),
                body.GetProperty("left").GetInt32()));
    }

    // =============================================================================================
    // Вітрина й HTTP
    // =============================================================================================

    [Fact]
    public void Shop_shows_curses_on_me_and_mine_worn_ring_and_fx()
    {
        Hex("Оля", "Петро");
        var e = Views.Json(LavkaSetup.Shop(As("Петро"), _lavka));
        var worn = e.GetProperty("worn");
        Assert.True(worn.TryGetProperty("fx", out _));
        Assert.True(worn.TryGetProperty("ring", out _));

        var curses = e.GetProperty("curses");
        Assert.Equal(["onMe", "mine"], curses.EnumerateObject().Select(p => p.Name));
        var on = Assert.Single(curses.GetProperty("onMe").EnumerateArray().ToList());
        Assert.Equal(["id", "item", "title", "emoji", "left", "at", "state"], on.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("goat", "Цап", "🐐", 3, "live"), (on.GetProperty("item").GetString(), on.GetProperty("title").GetString(),
            on.GetProperty("emoji").GetString(), on.GetProperty("left").GetInt32(), on.GetProperty("state").GetString()));
        Assert.Empty(curses.GetProperty("mine").EnumerateArray());

        var mine = Assert.Single(Views.Json(_lavka.View("Оля", true)).GetProperty("curses").GetProperty("mine").EnumerateArray().ToList());
        Assert.Equal(["id", "to", "item", "title", "emoji", "left", "at", "revealed", "state"], mine.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Петро", mine.GetProperty("to").GetString());
        Assert.False(mine.GetProperty("revealed").GetBoolean());

        // гість — порожні списки, ніякого чужого
        var guest = Views.Json(_lavka.View("гість Вася", false)).GetProperty("curses");
        Assert.Empty(guest.GetProperty("onMe").EnumerateArray());
        Assert.Empty(guest.GetProperty("mine").EnumerateArray());
    }

    [Fact]
    public void Curse_ransom_reveal_and_ring_endpoints_answer_with_the_usual_shape()
    {
        Give("Оля", 1000);
        var (status, body) = Radio.Reply(LavkaSetup.Curse(As("Оля"), new LavkaSetup.CurseRequest("Петро", "clown"), _lavka));
        Assert.Equal(200, status);
        Assert.Equal(["ok", "message", "balance"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Balance("Оля"), body.GetProperty("balance").GetInt32());
        var id = _store.LiveOn("Петро").Single().Id;

        var (guestStatus, guest) = Radio.Reply(LavkaSetup.Curse(As("гість Вася", false), new LavkaSetup.CurseRequest("Петро", "clown"), _lavka));
        Assert.Equal(400, guestStatus);
        Assert.False(guest.GetProperty("ok").GetBoolean());

        Give("Петро", 2000);
        var (revealStatus, reveal) = Radio.Reply(LavkaSetup.Reveal(As("Петро"), new LavkaSetup.CurseIdRequest(id), _lavka));
        Assert.Equal(200, revealStatus);
        Assert.Equal(["ok", "message", "balance"], reveal.EnumerateObject().Select(p => p.Name));
        var (ransomStatus, ransom) = Radio.Reply(LavkaSetup.Ransom(As("Петро"), new LavkaSetup.CurseIdRequest(id), _lavka));
        Assert.Equal(200, ransomStatus);
        Assert.Equal(Balance("Петро"), ransom.GetProperty("balance").GetInt32());
        var (againStatus, again) = Radio.Reply(LavkaSetup.Ransom(As("Петро"), new LavkaSetup.CurseIdRequest(id), _lavka));
        Assert.Equal(400, againStatus);
        Assert.Equal(Lavka.CurseGone, again.GetProperty("message").GetString());

        var (ringStatus, ring) = Radio.Reply(LavkaSetup.Ring(As("Оля"), new LavkaSetup.RingRequest("trembita"), _lavka));
        Assert.Equal(400, ringStatus);
        Assert.Equal(["ok", "message"], ring.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Lavka.NoRing, ring.GetProperty("message").GetString());
    }
}
