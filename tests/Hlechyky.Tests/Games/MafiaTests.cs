using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Мафія: ролі, фази за годинником, нічні дії наосліп, голосування і — головне — приховування. У цій грі
/// половина правил полягає в тому, чого гравець НЕ має бачити, тому вид кожного місця перевіряється саме
/// в тому JSON, який піде на дріт (TESTING.md §4, spec §Тести).
///
/// Ролі тест дізнається так само, як їх дізнається людина: зі свого виду (<c>me.role</c>). Жодних
/// «внутрішніх дверцят» у гру звідси нема — якщо роль не видно у виді, її не видно й тестові.
/// </summary>
public class MafiaTests
{
    static readonly string[] Villagers =
        ["Оля", "Петро", "Ганна", "Микола", "Іван", "Марія", "Тарас", "Соломія", "Богдан", "Леся", "Остап", "Дарина"];

    static RoomHarness Table(int players = 4, int seed = 1)
    {
        var h = new RoomHarness("mafia", seed: seed);
        for (var i = 0; i < players; i++) h.Join(Villagers[i]);
        h.Start();
        return h;
    }

    // ---------- як тест дивиться на село ----------

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static int Day(RoomHarness h) => h.View(null).GetProperty("day").GetInt32();

    static IEnumerable<JsonElement> Players(RoomHarness h, int? seat = null) =>
        h.View(seat).GetProperty("players").EnumerateArray();

    static Dictionary<int, string> Roles(RoomHarness h)
    {
        var map = new Dictionary<int, string>();
        for (var seat = 0; seat < h.Room.Seats.Length; seat++)
        {
            if (h.Room.Seats[seat] is null) continue;
            var me = h.View(seat).GetProperty("me");
            if (me.ValueKind == JsonValueKind.Object) map[seat] = me.GetProperty("role").GetString()!;
        }
        return map;
    }

    static int Seat(RoomHarness h, string role) => Roles(h).First(p => p.Value == role).Key;

    static int[] SeatsOf(RoomHarness h, string role) =>
        [.. Roles(h).Where(p => p.Value == role).Select(p => p.Key).Order()];

    static bool AliveAt(RoomHarness h, int seat) =>
        Players(h).First(p => p.GetProperty("seat").GetInt32() == seat).GetProperty("alive").GetBoolean();

    static int[] AliveSeats(RoomHarness h) =>
        [.. Players(h).Where(p => p.GetProperty("alive").GetBoolean()).Select(p => p.GetProperty("seat").GetInt32())];

    static string[] Log(RoomHarness h) =>
        [.. h.View(null).GetProperty("log").EnumerateArray().Select(x => x.GetString()!)];

    /// <summary>Тикати, доки не станеться те, чого чекаємо. Ліміт — щоб зациклений тест падав, а не висів.</summary>
    static void Until(RoomHarness h, Func<bool> done, int limit = 400)
    {
        for (var i = 0; i < limit && !done(); i++) h.Tick();
        Assert.True(done(), $"не дочекались: фаза {Phase(h)}, день {Day(h)}");
    }

    static void To(RoomHarness h, string phase) => Until(h, () => Phase(h) == phase);

    /// <summary>Прожити ніч: мафія б'є в <paramref name="kill"/>, комісар і лікар роблять, що сказано.</summary>
    static void PlayNight(RoomHarness h, int kill, int? check = null, int? heal = null)
    {
        To(h, "night");
        foreach (var (seat, role) in Roles(h))
        {
            if (!AliveAt(h, seat)) continue;
            if (role == "mafia") Assert.True(h.Act(seat, "kill", new { seat = kill }).Ok);
            if (role == "sheriff" && check is { } c) h.Act(seat, "check", new { seat = c });
            if (role == "doctor" && heal is { } x) h.Act(seat, "heal", new { seat = x });
        }
        Until(h, () => Phase(h) != "night");
    }

    /// <summary>Прожити голосування рівно з тими голосами, які просить тест (null — утриматись).</summary>
    static void PlayVote(RoomHarness h, IReadOnlyDictionary<int, int?> votes)
    {
        To(h, "vote");
        foreach (var (seat, target) in votes)
        {
            object? payload = target is { } t ? new { seat = t } : null;
            h.Act(seat, "vote", payload);
        }
        Until(h, () => Phase(h) != "vote");
    }

    /// <summary>Живе місце, яке не мафія і не одне з названих.</summary>
    static int OtherCivil(RoomHarness h, params int[] except)
    {
        var mafia = SeatsOf(h, "mafia");
        return AliveSeats(h).First(s => !mafia.Contains(s) && !except.Contains(s));
    }

    // =========================================================================================
    // Роздача ролей
    // =========================================================================================

    [Fact]
    public void The_cast_follows_the_table_for_every_size()
    {
        for (var n = 4; n <= 12; n++)
        {
            var (mafia, sheriff, doctor) = Mafia.Cast(n);
            var want = n <= 5 ? (1, 1, 0) : n <= 8 ? (2, 1, 1) : (3, 1, 1);
            Assert.Equal(want, (mafia, sheriff, doctor));
            Assert.True(mafia + sheriff + doctor <= n, $"на {n} гравців ролей більше, ніж людей");
        }
    }

    [Fact]
    public void Four_players_get_one_mafia_one_sheriff_and_no_doctor()
    {
        var h = Table(4);
        var roles = Roles(h);

        Assert.Equal(4, roles.Count);
        Assert.Single(roles.Values, r => r == "mafia");
        Assert.Single(roles.Values, r => r == "sheriff");
        Assert.DoesNotContain("doctor", roles.Values);
        Assert.Equal(2, roles.Values.Count(r => r == "civil"));
    }

    [Fact]
    public void Six_players_get_a_second_mafia_and_a_doctor()
    {
        var roles = Roles(Table(6));

        Assert.Equal(2, roles.Values.Count(r => r == "mafia"));
        Assert.Single(roles.Values, r => r == "sheriff");
        Assert.Single(roles.Values, r => r == "doctor");
        Assert.Equal(2, roles.Values.Count(r => r == "civil"));
    }

    [Fact]
    public void Nine_players_get_three_mafia()
    {
        var roles = Roles(Table(9));

        Assert.Equal(3, roles.Values.Count(r => r == "mafia"));
        Assert.Single(roles.Values, r => r == "sheriff");
        Assert.Single(roles.Values, r => r == "doctor");
        Assert.Equal(4, roles.Values.Count(r => r == "civil"));
    }

    [Fact]
    public void A_full_table_of_twelve_still_gets_three_mafia()
    {
        var roles = Roles(Table(12));

        Assert.Equal(12, roles.Count);
        Assert.Equal(3, roles.Values.Count(r => r == "mafia"));
        Assert.Equal(7, roles.Values.Count(r => r == "civil"));
    }

    [Fact]
    public void Mafia_knows_its_own_and_nobody_else()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");

        foreach (var player in Players(h, mafia[0]))
        {
            var seat = player.GetProperty("seat").GetInt32();
            var role = player.GetProperty("role");
            if (mafia.Contains(seat)) Assert.Equal("mafia", role.GetString());
            else Assert.Equal(JsonValueKind.Null, role.ValueKind);
        }
    }

    [Fact]
    public void The_same_seed_deals_the_same_roles()
    {
        Assert.Equal(Roles(Table(8, seed: 77)), Roles(Table(8, seed: 77)));
    }

    [Fact]
    public void The_same_seed_and_the_same_deeds_give_the_same_views()
    {
        // TESTING.md §4.3: та сама послідовність дій із тим самим сідом — той самий вид на дроті.
        // У мафії від Ctx.Rng залежить не лише роздача, а й вибір реплік Глека, тож звіряємо і їх.
        static RoomHarness Played()
        {
            var h = Table(6, seed: 7);
            var mafia = SeatsOf(h, "mafia");
            var doctor = Seat(h, "doctor");
            To(h, "night");
            h.Act(mafia[0], "say", new { text = "беремо старосту" });
            PlayNight(h, OtherCivil(h, doctor), check: mafia[1], heal: doctor);
            To(h, "vote");
            PlayVote(h, AliveSeats(h).Take(3).ToDictionary(s => s, _ => (int?)mafia[0]));
            return h;
        }

        var a = Played();
        var b = Played();

        for (var seat = 0; seat < 6; seat++)
            Assert.Equal(Views.Text(a.Room.Game.View(seat)), Views.Text(b.Room.Game.View(seat)));
        Assert.Equal(Views.Text(a.Room.Game.View(null)), Views.Text(b.Room.Game.View(null)));
        Assert.Equal(
            a.Outbox.OfType<DjSays>().Select(x => x.Text).ToArray(),
            b.Outbox.OfType<DjSays>().Select(x => x.Text).ToArray());
    }

    // =========================================================================================
    // Фази за годинником
    // =========================================================================================

    [Fact]
    public void The_game_opens_with_the_intro_and_a_word_from_hlek()
    {
        var h = Table();

        Assert.Equal("intro", Phase(h));
        Assert.Equal(1, Day(h));
        Assert.NotEmpty(h.Outbox.OfType<DjSays>());
        Assert.All(Players(h), p => Assert.True(p.GetProperty("alive").GetBoolean()));
    }

    [Fact]
    public void The_intro_lasts_exactly_ten_seconds()
    {
        var h = Table();
        h.Tick(Mafia.IntroMs / Mafia.TickMs - 1);
        Assert.Equal("intro", Phase(h));

        h.Tick();
        Assert.Equal("night", Phase(h));
    }

    [Fact]
    public void The_night_ends_early_when_everyone_has_acted()
    {
        var h = Table(6);
        To(h, "night");
        var kill = OtherCivil(h);
        var mafia = SeatsOf(h, "mafia");
        foreach (var seat in mafia) h.Act(seat, "kill", new { seat = kill });
        h.Act(Seat(h, "sheriff"), "check", new { seat = mafia[0] });
        h.Act(Seat(h, "doctor"), "heal", new { seat = mafia[0] });

        h.Tick();
        Assert.Equal("day", Phase(h));
    }

    [Fact]
    public void The_night_runs_its_full_length_when_the_sheriff_sleeps()
    {
        var h = Table(6);
        To(h, "night");
        var kill = OtherCivil(h);
        foreach (var seat in SeatsOf(h, "mafia")) h.Act(seat, "kill", new { seat = kill });

        h.Tick(Mafia.NightMs / Mafia.TickMs - 1);
        Assert.Equal("night", Phase(h));
        h.Tick();
        Assert.Equal("day", Phase(h));
    }

    [Fact]
    public void Day_turns_into_vote_and_a_vote_starts_the_next_day()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h), check: mafia[0], heal: mafia[0]);
        Assert.Equal(1, Day(h));

        To(h, "vote");
        PlayVote(h, new Dictionary<int, int?>());
        Assert.Equal("night", Phase(h));
        Assert.Equal(2, Day(h));
    }

    [Fact]
    public void Every_phase_carries_its_own_deadline()
    {
        var h = Table();
        var intro = h.View(null).GetProperty("endsAt").GetDateTimeOffset();
        To(h, "night");
        var night = h.View(null).GetProperty("endsAt").GetDateTimeOffset();

        Assert.True(night > intro, "дедлайн ночі має бути пізніший за дедлайн знайомства");
        Assert.Equal(Mafia.NightMs, (night - intro).TotalMilliseconds);
    }

    // =========================================================================================
    // Ніч
    // =========================================================================================

    [Fact]
    public void The_mafia_majority_picks_the_victim()
    {
        var h = Table(9);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia");
        var a = OtherCivil(h);
        var b = OtherCivil(h, a);

        h.Act(mafia[0], "kill", new { seat = a });
        h.Act(mafia[1], "kill", new { seat = b });
        h.Act(mafia[2], "kill", new { seat = b });
        Until(h, () => Phase(h) != "night");

        Assert.False(AliveAt(h, b));
        Assert.True(AliveAt(h, a));
    }

    [Fact]
    public void A_tie_goes_to_the_one_the_mafia_named_first()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia");
        var first = OtherCivil(h);
        var second = OtherCivil(h, first);

        h.Act(mafia[0], "kill", new { seat = first });
        h.Act(mafia[1], "kill", new { seat = second });
        Until(h, () => Phase(h) != "night");

        Assert.False(AliveAt(h, first));
        Assert.True(AliveAt(h, second));
    }

    [Fact]
    public void The_doctor_saves_the_one_the_mafia_came_for()
    {
        var h = Table(6);
        To(h, "night");
        var victim = OtherCivil(h, Seat(h, "doctor"));
        PlayNight(h, victim, check: SeatsOf(h, "mafia")[0], heal: victim);

        Assert.True(AliveAt(h, victim));
        var info = h.View(null).GetProperty("dayInfo");
        Assert.True(info.GetProperty("saved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("killed").ValueKind);
        Assert.Contains("усі вціліли", Log(h)[0]);
    }

    [Fact]
    public void Without_the_doctor_the_victim_is_gone_and_the_log_says_so()
    {
        var h = Table(6);
        To(h, "night");
        var victim = OtherCivil(h, Seat(h, "doctor"));
        PlayNight(h, victim, check: SeatsOf(h, "mafia")[0], heal: Seat(h, "doctor"));

        Assert.False(AliveAt(h, victim));
        Assert.Equal(victim, h.View(null).GetProperty("dayInfo").GetProperty("killed").GetInt32());
        Assert.Contains("не прокинувся", Log(h)[0]);
    }

    [Fact]
    public void The_doctor_may_save_himself()
    {
        var h = Table(6);
        To(h, "night");
        var doctor = Seat(h, "doctor");

        Assert.True(h.Act(doctor, "heal", new { seat = doctor }).Ok);
        Assert.Equal(doctor, h.View(doctor).GetProperty("night").GetProperty("healed").GetInt32());
    }

    [Fact]
    public void The_doctor_cannot_save_the_same_person_two_nights_running()
    {
        var h = Table(6);
        To(h, "night");
        var doctor = Seat(h, "doctor");
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, doctor), check: mafia[0], heal: doctor);

        PlayVote(h, new Dictionary<int, int?>());
        To(h, "night");

        var again = h.Act(doctor, "heal", new { seat = doctor });
        Assert.False(again.Ok);
        Assert.Equal("Цю людину ти рятував минулої ночі", again.Message);
        Assert.True(h.Act(doctor, "heal", new { seat = mafia[0] }).Ok);
    }

    [Fact]
    public void A_doctor_who_left_the_village_saves_nobody()
    {
        var h = Table(9);
        To(h, "night");
        var doctor = Seat(h, "doctor");
        var victim = OtherCivil(h, doctor);
        Assert.True(h.Act(doctor, "heal", new { seat = victim }).Ok);

        // Лікар устав з-за столу вже після того, як показав, кого рятує: рятувати тепер нема кому.
        h.Leave(h.NickOf(doctor));
        foreach (var seat in SeatsOf(h, "mafia")) Assert.True(h.Act(seat, "kill", new { seat = victim }).Ok);
        Until(h, () => Phase(h) != "night");

        Assert.False(AliveAt(h, victim));
        Assert.Contains(Log(h), l => l.Contains("не прокинувся"));
        Assert.False(h.View(null).GetProperty("dayInfo").GetProperty("saved").GetBoolean());
    }

    [Fact]
    public void The_sheriff_checks_once_a_night()
    {
        var h = Table(6);
        To(h, "night");
        var sheriff = Seat(h, "sheriff");
        var mafia = SeatsOf(h, "mafia");

        Assert.True(h.Act(sheriff, "check", new { seat = mafia[0] }).Ok);
        var before = Views.Text(h.Room.Game.View(sheriff));

        var second = h.Act(sheriff, "check", new { seat = mafia[1] });
        Assert.False(second.Ok);
        Assert.Equal("Цієї ночі ти вже перевіряв", second.Message);
        // Відмова має бути повною: другий мафіозі не потрапив у список перевірених (TESTING.md §4.1).
        Assert.Equal(before, Views.Text(h.Room.Game.View(sheriff)));
    }

    [Fact]
    public void The_sheriff_cannot_check_himself()
    {
        var h = Table(6);
        To(h, "night");
        var sheriff = Seat(h, "sheriff");

        var no = h.Act(sheriff, "check", new { seat = sheriff });
        Assert.False(no.Ok);
        Assert.Equal("Себе ти й так знаєш", no.Message);
    }

    [Fact]
    public void A_check_answers_the_sheriff_and_piles_up_in_his_view()
    {
        var h = Table(6);
        To(h, "night");
        var sheriff = Seat(h, "sheriff");
        var mafia = SeatsOf(h, "mafia")[0];

        var reply = h.Act(sheriff, "check", new { seat = mafia });
        Assert.Contains("мафія", reply.Message);
        var checks = h.View(sheriff).GetProperty("night").GetProperty("myCheck").EnumerateArray().ToArray();
        Assert.Single(checks);
        Assert.Equal(mafia, checks[0].GetProperty("seat").GetInt32());
        Assert.True(checks[0].GetProperty("mafia").GetBoolean());
    }

    [Fact]
    public void The_mafia_does_not_touch_its_own()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia");

        var before = Views.Text(h.Room.Game.View(mafia[0]));
        var no = h.Act(mafia[0], "kill", new { seat = mafia[1] });

        Assert.False(no.Ok);
        Assert.Equal("Своїх не чіпаємо", no.Message);
        // Ніж не має лягти в стан раніше за перевірку: вид до й після відмови однаковий (TESTING.md §4.1).
        Assert.Equal(before, Views.Text(h.Room.Game.View(mafia[0])));
    }

    [Fact]
    public void A_civil_has_nothing_to_do_at_night()
    {
        var h = Table(6);
        To(h, "night");
        var civil = Seat(h, "civil");
        var other = OtherCivil(h, civil);

        foreach (var action in new[] { "kill", "check", "heal", "say" })
        {
            var no = h.Act(civil, action, new { seat = other, text = "агов" });
            Assert.False(no.Ok);
            Assert.Equal("Це не твоя справа", no.Message);
        }
    }

    [Fact]
    public void Night_deeds_are_refused_in_daylight()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h), check: mafia[0], heal: mafia[0]);

        var no = h.Act(mafia[0], "kill", new { seat = OtherCivil(h) });
        Assert.False(no.Ok);
        Assert.Equal("Зараз не час", no.Message);
    }

    [Fact]
    public void Nobody_kills_the_already_dead()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        var victim = OtherCivil(h, Seat(h, "doctor"));
        PlayNight(h, victim, check: mafia[0], heal: mafia[0]);
        PlayVote(h, new Dictionary<int, int?>());
        To(h, "night");

        var no = h.Act(mafia[0], "kill", new { seat = victim });
        Assert.False(no.Ok);
        Assert.Equal("Його вже нема серед живих", no.Message);
    }

    [Fact]
    public void The_dead_keep_quiet()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        var victim = OtherCivil(h, Seat(h, "doctor"));
        PlayNight(h, victim, check: mafia[0], heal: mafia[0]);
        To(h, "vote");

        var no = h.Act(victim, "vote", new { seat = mafia[0] });
        Assert.False(no.Ok);
        Assert.Equal("Мертві мовчать", no.Message);
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        var h = Table();
        Assert.Equal("Тут так не ходять", h.Act(0, "танцювати", new { seat = 1 }).Message);
    }

    // =========================================================================================
    // Нічний чат мафії
    // =========================================================================================

    [Fact]
    public void The_mafia_whispers_and_sees_the_whisper()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia");

        Assert.True(h.Act(mafia[0], "say", new { text = "беремо Ганну" }).Ok);
        var chat = h.View(mafia[1]).GetProperty("night").GetProperty("chat").EnumerateArray().ToArray();
        Assert.Single(chat);
        Assert.Equal(mafia[0], chat[0].GetProperty("seat").GetInt32());
        Assert.Equal("беремо Ганну", chat[0].GetProperty("text").GetString());
    }

    [Fact]
    public void An_empty_whisper_is_refused()
    {
        var h = Table(6);
        To(h, "night");

        var no = h.Act(SeatsOf(h, "mafia")[0], "say", new { text = "   " });
        Assert.False(no.Ok);
        Assert.Equal("Порожнє нікому не цікаво", no.Message);
    }

    [Fact]
    public void A_long_whisper_is_cut_short()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia")[0];

        h.Act(mafia, "say", new { text = new string('я', Mafia.MaxSayChars + 40) });
        var chat = h.View(mafia).GetProperty("night").GetProperty("chat").EnumerateArray().ToArray();
        Assert.Equal(Mafia.MaxSayChars, chat[0].GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public void The_night_chat_keeps_only_the_last_fifty_lines()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia")[0];
        for (var i = 0; i < Mafia.MaxChatLines + 5; i++) h.Act(mafia, "say", new { text = "рядок " + i });

        var chat = h.View(mafia).GetProperty("night").GetProperty("chat").EnumerateArray().ToArray();
        Assert.Equal(Mafia.MaxChatLines, chat.Length);
        Assert.Equal("рядок 5", chat[0].GetProperty("text").GetString());
    }

    // =========================================================================================
    // Голосування
    // =========================================================================================

    [Fact]
    public void A_majority_of_the_living_exiles_a_player_and_shows_the_role()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        To(h, "vote");

        var alive = AliveSeats(h);
        var target = mafia[0];
        var votes = alive.Where(s => s != target).ToDictionary(s => s, _ => (int?)target);
        PlayVote(h, votes);

        Assert.False(AliveAt(h, target));
        Assert.Contains(Log(h), l => l.Contains("іде за ворота") && l.Contains("мафія"));
        // Роль вигнаного бачить усе село — так само, як її щойно оголосили в хроніці.
        var seen = Players(h, OtherCivil(h)).First(p => p.GetProperty("seat").GetInt32() == target);
        Assert.Equal("mafia", seen.GetProperty("role").GetString());
    }

    [Fact]
    public void A_tie_exiles_nobody()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        To(h, "vote");

        var alive = AliveSeats(h);
        var votes = new Dictionary<int, int?> { [alive[0]] = alive[2], [alive[1]] = alive[3] };
        PlayVote(h, votes);

        Assert.Equal(alive.Length, AliveSeats(h).Length);
        Assert.Contains(Log(h), l => l.Contains("не дійшло згоди"));
    }

    [Fact]
    public void Exactly_half_the_village_is_not_a_majority()
    {
        var h = Table(6);
        var doctor = Seat(h, "doctor");
        var victim = OtherCivil(h, doctor);
        // Лікар устиг, тож до голосування доживають усі шестеро — і половина села рівно три голоси.
        PlayNight(h, victim, heal: victim);
        To(h, "vote");

        var alive = AliveSeats(h);
        Assert.Equal(6, alive.Length);
        PlayVote(h, alive.Take(3).ToDictionary(s => s, _ => (int?)alive[5]));

        Assert.True(AliveAt(h, alive[5]));
        Assert.Contains(Log(h), l => l.Contains("не дійшло згоди"));
    }

    [Fact]
    public void The_loudest_group_still_needs_more_than_half()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        To(h, "vote");

        // П'ятеро живих, два голоси проти одного: більше за всіх — ще не більшість села.
        var alive = AliveSeats(h);
        Assert.Equal(5, alive.Length);
        PlayVote(h, new Dictionary<int, int?>
        {
            [alive[0]] = alive[2], [alive[1]] = alive[2], [alive[3]] = alive[4],
        });

        Assert.Equal(alive.Length, AliveSeats(h).Length);
        Assert.Contains(Log(h), l => l.Contains("не дійшло згоди"));
    }

    [Fact]
    public void Abstaining_never_exiles_anybody()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        To(h, "vote");

        var alive = AliveSeats(h);
        var reply = h.Act(alive[0], "vote", null);
        Assert.True(reply.Ok);
        Assert.Equal("Утримався", reply.Message);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("votes").GetProperty(alive[0].ToString()).ValueKind);

        Until(h, () => Phase(h) != "vote");
        Assert.Equal(alive.Length, AliveSeats(h).Length);
    }

    [Fact]
    public void A_vote_can_be_changed_until_the_phase_ends()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        To(h, "vote");

        var alive = AliveSeats(h);
        h.Act(alive[0], "vote", new { seat = alive[1] });
        Assert.Equal(alive[1], h.View(null).GetProperty("votes").GetProperty(alive[0].ToString()).GetInt32());

        h.Act(alive[0], "vote", new { seat = alive[2] });
        Assert.Equal(alive[2], h.View(null).GetProperty("votes").GetProperty(alive[0].ToString()).GetInt32());
    }

    [Fact]
    public void Votes_are_open_only_while_the_village_votes()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        Assert.Empty(h.View(null).GetProperty("votes").EnumerateObject());

        To(h, "vote");
        var alive = AliveSeats(h);
        h.Act(alive[0], "vote", new { seat = alive[1] });
        // Голоси відкриті: їх бачить навіть той, хто дивиться збоку.
        Assert.Equal(alive[1], h.View(null).GetProperty("votes").GetProperty(alive[0].ToString()).GetInt32());
    }

    [Fact]
    public void Voting_out_of_turn_is_refused()
    {
        var h = Table(6);
        To(h, "night");

        var no = h.Act(AliveSeats(h)[0], "vote", new { seat = AliveSeats(h)[1] });
        Assert.False(no.Ok);
        Assert.Equal("Зараз не час", no.Message);
    }

    [Fact]
    public void Voting_for_the_dead_is_refused()
    {
        var h = Table(6);
        var mafia = SeatsOf(h, "mafia");
        var victim = OtherCivil(h, Seat(h, "doctor"));
        PlayNight(h, victim, check: mafia[0], heal: mafia[0]);
        To(h, "vote");

        var no = h.Act(AliveSeats(h)[0], "vote", new { seat = victim });
        Assert.False(no.Ok);
        Assert.Equal("Його вже нема серед живих", no.Message);
    }

    // =========================================================================================
    // Кінець партії
    // =========================================================================================

    [Fact]
    public void The_village_wins_when_the_last_mafia_is_exiled()
    {
        var h = Table(4);
        var mafia = Seat(h, "mafia");
        PlayNight(h, OtherCivil(h));
        To(h, "vote");

        var votes = AliveSeats(h).Where(s => s != mafia).ToDictionary(s => s, _ => (int?)mafia);
        PlayVote(h, votes);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("civil", h.View(null).GetProperty("result").GetProperty("team").GetString());
        Assert.Contains("перемогли мирні", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void The_winners_include_the_dead_of_the_winning_team()
    {
        var h = Table(4);
        var mafia = Seat(h, "mafia");
        var victim = OtherCivil(h);
        PlayNight(h, victim);
        To(h, "vote");
        PlayVote(h, AliveSeats(h).Where(s => s != mafia).ToDictionary(s => s, _ => (int?)mafia));

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Contains(victim, h.Room.Result!.Winners);          // мертвий, але свій
        Assert.DoesNotContain(mafia, h.Room.Result!.Winners);
        Assert.Equal(3, h.Room.Result!.Winners.Length);
    }

    [Fact]
    public void The_mafia_wins_when_it_is_no_longer_outnumbered()
    {
        var h = Table(4);
        var mafia = Seat(h, "mafia");
        var sheriff = Seat(h, "sheriff");
        PlayNight(h, OtherCivil(h, sheriff));
        PlayVote(h, new Dictionary<int, int?>());
        PlayNight(h, sheriff);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("mafia", h.View(null).GetProperty("result").GetProperty("team").GetString());
        Assert.Equal([mafia], h.Room.Result!.Winners);
    }

    [Fact]
    public void A_mafia_win_asks_for_its_own_achievement()
    {
        var h = Table(4);
        var sheriff = Seat(h, "sheriff");
        var mafia = Seat(h, "mafia");
        PlayNight(h, OtherCivil(h, sheriff));
        PlayVote(h, new Dictionary<int, int?>());
        PlayNight(h, sheriff);

        var award = Assert.Single(h.Awards, a => a.Reason == "ach:mafia-win");
        Assert.Equal(h.Room.Seats[mafia], award.Nick);
        Assert.Equal(0, award.Shards);   // черепки платить сама ачівка з каталогу
    }

    [Fact]
    public void The_sheriff_achievement_comes_from_a_check_that_found_mafia()
    {
        var h = Table(6);
        To(h, "night");
        var sheriff = Seat(h, "sheriff");
        h.Act(sheriff, "check", new { seat = OtherCivil(h, sheriff) });
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:sheriff");

        PlayNight(h, OtherCivil(h, sheriff, Seat(h, "doctor")), heal: Seat(h, "doctor"));
        PlayVote(h, new Dictionary<int, int?>());
        To(h, "night");
        h.Act(sheriff, "check", new { seat = SeatsOf(h, "mafia")[0] });

        var award = Assert.Single(h.Awards, a => a.Reason == "ach:sheriff");
        Assert.Equal(h.Room.Seats[sheriff], award.Nick);
    }

    [Fact]
    public void A_player_who_leaves_the_village_counts_as_dead_and_is_revealed()
    {
        var h = Table(9);
        var roles = Roles(h);
        var civil = roles.First(p => p.Value == "civil").Key;
        var watcher = roles.First(p => p.Value == "civil" && p.Key != civil).Key;
        h.Leave(h.NickOf(civil));

        Assert.False(AliveAt(h, civil));
        Assert.Contains(Log(h), l => l.Contains("виїхав із села"));
        var seen = Players(h, watcher).First(p => p.GetProperty("seat").GetInt32() == civil);
        Assert.Equal("civil", seen.GetProperty("role").GetString());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_departure_at_night_is_written_down_as_a_night()
    {
        var h = Table(9);
        To(h, "night");
        var civil = Seat(h, "civil");

        h.Leave(h.NickOf(civil));

        Assert.Contains(Log(h), l => l.StartsWith("Ніч 1:") && l.Contains("виїхав із села"));
        Assert.DoesNotContain(Log(h), l => l.StartsWith("День") && l.Contains("виїхав із села"));
    }

    [Fact]
    public void Leaving_can_hand_the_village_its_victory()
    {
        var h = Table(4);
        var mafia = Seat(h, "mafia");
        h.Leave(h.NickOf(mafia));

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("civil", h.View(null).GetProperty("result").GetProperty("team").GetString());
    }

    [Fact]
    public void A_village_of_fewer_than_three_falls_apart_into_a_draw()
    {
        var h = Table(4);
        var mafia = Seat(h, "mafia");
        var rest = Roles(h).Keys.Where(s => s != mafia).ToArray();
        h.Leave(h.NickOf(rest[0]));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);

        h.Leave(h.NickOf(rest[1]));

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Equal("draw", h.View(null).GetProperty("result").GetProperty("team").GetString());
    }

    [Fact]
    public void After_the_game_nothing_is_accepted_anymore()
    {
        var h = Table(4);
        h.Leave(h.NickOf(Seat(h, "mafia")));

        Assert.Equal("Партію зіграно, тисни «Ще раз»", h.Act(0, "vote", new { seat = 1 }).Message);
    }

    // =========================================================================================
    // Приховування — п'ять окремих перевірок (spec §Вид)
    // =========================================================================================

    [Fact]
    public void Hiding_a_civil_sees_no_role_but_his_own()
    {
        var h = Table(6);
        var civil = Seat(h, "civil");

        foreach (var player in Players(h, civil))
        {
            var seat = player.GetProperty("seat").GetInt32();
            var role = player.GetProperty("role");
            if (seat == civil) Assert.Equal("civil", role.GetString());
            else Assert.Equal(JsonValueKind.Null, role.ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, h.View(civil).GetProperty("night").ValueKind);
    }

    [Fact]
    public void Hiding_the_sheriff_learns_only_what_he_checked()
    {
        var h = Table(6);
        To(h, "night");
        var sheriff = Seat(h, "sheriff");
        var mafia = SeatsOf(h, "mafia")[0];
        h.Act(sheriff, "check", new { seat = mafia });

        // Знання комісара живе окремо, у його нічному блоці: у списку гравців ролі так і лишаються закриті.
        var view = h.View(sheriff);
        foreach (var player in view.GetProperty("players").EnumerateArray())
        {
            var seat = player.GetProperty("seat").GetInt32();
            if (seat == sheriff) continue;
            Assert.Equal(JsonValueKind.Null, player.GetProperty("role").ValueKind);
        }
        var checks = view.GetProperty("night").GetProperty("myCheck").EnumerateArray().ToArray();
        Assert.Equal(mafia, Assert.Single(checks).GetProperty("seat").GetInt32());
        Assert.Empty(view.GetProperty("night").GetProperty("votes").EnumerateObject());
        Assert.Empty(view.GetProperty("night").GetProperty("chat").EnumerateArray());
    }

    [Fact]
    public void Hiding_a_watcher_sees_only_the_public_village()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia");
        h.Act(mafia[0], "say", new { text = "тихо, беремо старосту" });
        h.Act(mafia[0], "kill", new { seat = OtherCivil(h) });

        var text = Views.Text(h.Room.Game.View(null));
        var view = h.View(null);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("night").ValueKind);
        Assert.All(view.GetProperty("players").EnumerateArray(),
            p => Assert.Equal(JsonValueKind.Null, p.GetProperty("role").ValueKind));
        Assert.DoesNotContain("тихо, беремо старосту", text);
        Assert.True(Views.Has(view, "log"));
    }

    [Fact]
    public void Hiding_the_night_chat_never_reaches_a_civil()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia");
        var civil = Seat(h, "civil");
        h.Act(mafia[0], "say", new { text = "шепіт під вікном" });
        h.Act(mafia[0], "kill", new { seat = civil });

        var civilText = Views.Text(h.Room.Game.View(civil));
        Assert.DoesNotContain("шепіт під вікном", civilText);
        Assert.Equal(JsonValueKind.Null, h.View(civil).GetProperty("night").ValueKind);
        // а мафія свій шепіт і свій вибір бачить
        var night = h.View(mafia[1]).GetProperty("night");
        Assert.Single(night.GetProperty("chat").EnumerateArray());
        Assert.Equal(civil, night.GetProperty("votes").GetProperty(mafia[0].ToString()).GetInt32());
    }

    [Fact]
    public void Hiding_the_dead_see_everything()
    {
        var h = Table(6);
        To(h, "night");
        var mafia = SeatsOf(h, "mafia");
        var victim = OtherCivil(h, Seat(h, "doctor"));
        h.Act(mafia[0], "say", new { text = "домовились" });
        PlayNight(h, victim, check: mafia[0], heal: Seat(h, "doctor"));

        var view = h.View(victim);
        Assert.All(view.GetProperty("players").EnumerateArray(),
            p => Assert.NotEqual(JsonValueKind.Null, p.GetProperty("role").ValueKind));
        Assert.Single(view.GetProperty("night").GetProperty("chat").EnumerateArray());
        Assert.False(view.GetProperty("me").GetProperty("alive").GetBoolean());
    }

    // =========================================================================================
    // Форма на дроті, каталог, рематч, збереження
    // =========================================================================================

    [Fact]
    public void A_newcomer_at_a_finished_table_gets_nobody_elses_role()
    {
        var h = Table(4);
        var mafia = Seat(h, "mafia");
        h.Leave(h.NickOf(mafia));                       // партія скінчилась, місце звільнилось
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        Assert.True(h.Join("Дарина").Ok);               // каркас відкриває дограний стіл наново
        var seat = Array.IndexOf(h.Room.Seats, "Дарина");
        Assert.Equal(mafia, seat);

        // Дарина за цим столом ще не грала: ні ролі, ні чужого ніка, ні чужої смерті.
        var view = h.View(seat);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("night").ValueKind);
        var mine = view.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);
        Assert.Equal("Дарина", mine.GetProperty("nick").GetString());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("role").ValueKind);
        Assert.True(mine.GetProperty("alive").GetBoolean());
    }

    [Fact]
    public void The_view_has_the_shape_the_spec_asks_for()
    {
        var h = Table(6);
        var view = h.View(Seat(h, "sheriff"));

        foreach (var name in new[] { "phase", "day", "endsAt", "players", "me", "night", "dayInfo", "votes", "log", "result" })
            Assert.True(Views.Has(view, name), name);
        foreach (var name in new[] { "seat", "nick", "alive", "role" })
            Assert.True(Views.Has(view.GetProperty("players")[0], name), name);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("dayInfo").ValueKind);
        Assert.Equal(Villagers[0], view.GetProperty("players")[0].GetProperty("nick").GetString());
    }

    [Fact]
    public void The_frame_is_public_and_short()
    {
        var h = Table(6);
        To(h, "night");
        var frame = Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

        foreach (var name in new[] { "phase", "day", "endsAt", "alive" })
            Assert.True(Views.Has(frame, name), name);
        foreach (var name in new[] { "players", "night", "log", "me" })
            Assert.False(Views.Has(frame, name), name);
        Assert.Equal("night", frame.GetProperty("phase").GetString());
        Assert.Equal(6, frame.GetProperty("alive").GetArrayLength());
    }

    [Fact]
    public void A_move_makes_the_next_tick_carry_the_fresh_views()
    {
        var h = Table(6);
        To(h, "night");
        var before = h.Outbox.OfType<RoomViews>().Count();
        // Реалтайм-кімнати каркас після Act видами не смикає (Rooms.Act), тож це робить тик.
        h.Act(SeatsOf(h, "mafia")[0], "say", new { text = "агов" });
        Assert.Equal(before, h.Outbox.OfType<RoomViews>().Count());

        h.Tick();
        Assert.True(h.Outbox.OfType<RoomViews>().Count() > before);
    }

    [Fact]
    public void Mafia_is_a_hidden_party_game_in_the_catalog()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "mafia");

        Assert.Equal("party", game.Group);
        Assert.True(game.Hidden);
        Assert.False(game.Rated);
        Assert.Equal("byHost", game.Start);
        Assert.Equal(4, game.MinPlayers);
        Assert.Equal(12, game.MaxPlayers);
        Assert.Equal(Mafia.TickMs, game.TickMs);
        Assert.Equal("mafia", game.Module);
    }

    [Fact]
    public void Seats_are_nameless_because_roles_are_a_secret()
    {
        var h = Table();
        Assert.Equal("гравець 1", h.Room.SafeSeatName(0));
        Assert.Equal("гравець 12", h.Room.SafeSeatName(11));
    }

    [Fact]
    public void A_rematch_deals_again_and_wakes_the_whole_village()
    {
        var h = Table(4);
        var sheriff = Seat(h, "sheriff");
        PlayNight(h, OtherCivil(h, sheriff));
        PlayVote(h, new Dictionary<int, int?>());
        PlayNight(h, sheriff);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        Assert.True(h.Rematch(Villagers[1]).Ok);

        // «Ще раз» обертає місця, тож роздача — з нуля: жодного мертвого, жодного рядка старої хроніки.
        Assert.Equal(Villagers[1], h.Room.Seats[0]);
        Assert.Equal("intro", Phase(h));
        Assert.Equal(1, Day(h));
        Assert.Empty(Log(h));
        Assert.All(Players(h), p => Assert.True(p.GetProperty("alive").GetBoolean()));
        Assert.Equal(4, Roles(h).Count);
        Assert.Single(Roles(h).Values, r => r == "mafia");
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Load_of_a_save_gives_the_very_same_view()
    {
        var h = Table(6, seed: 5);
        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        var saved = h.Room.Game.Save()!;

        var other = Table(6, seed: 9);
        other.Room.Game.Load(saved);

        Assert.Equal(Views.Text(h.Room.Game.View(mafia[0])), Views.Text(other.Room.Game.View(mafia[0])));
        Assert.Equal(Views.Text(h.Room.Game.View(null)), Views.Text(other.Room.Game.View(null)));
    }

    [Fact]
    public void Hlek_has_a_bank_of_at_least_twenty_five_lines()
    {
        Assert.True(MafiaGlek.All.Count >= 25, $"реплік лише {MafiaGlek.All.Count}");
        Assert.Equal(MafiaGlek.All.Count, MafiaGlek.All.Distinct().Count());
        // Підстановки мають бути справжні: {0} у шаблоні без аргументів кинув би на порожньому місці.
        Assert.All(MafiaGlek.Killed, line => Assert.Contains("{0}", line));
        Assert.All(MafiaGlek.Exiled, line => Assert.Contains("{1}", line));
    }

    [Fact]
    public void Hlek_says_exactly_one_word_on_every_phase_change()
    {
        var h = Table(6);
        int Said() => h.Outbox.OfType<DjSays>().Count();
        Assert.Equal(1, Said());   // знайомство Глек оголосив уже на старті

        To(h, "night");
        Assert.Equal(2, Said());

        var mafia = SeatsOf(h, "mafia");
        PlayNight(h, OtherCivil(h, Seat(h, "doctor")), check: mafia[0], heal: mafia[0]);
        Assert.Equal("day", Phase(h));
        // Підсумок ночі приліплений першим реченням до ранкової репліки — тому все одно рівно одна.
        Assert.Equal(3, Said());

        To(h, "vote");
        Assert.Equal(4, Said());
        Assert.All(h.Outbox.OfType<DjSays>(), s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }

    [Fact]
    public void A_word_left_in_the_queue_by_the_model_is_said_on_the_next_tick()
    {
        var h = Table(6);
        var game = (Mafia)h.Room.Game;
        var said = h.Outbox.OfType<DjSays>().Count();

        // Так у чергу лягає відповідь DjBrain.FlavorAsync: не з тика й не під замком кімнати.
        game.QueueLine("а я ж казав, що добром це не скінчиться");
        Assert.Equal(1, game.PendingLines);
        Assert.Equal(said, h.Outbox.OfType<DjSays>().Count());

        h.Tick();

        Assert.Equal(0, game.PendingLines);
        Assert.Contains(h.Outbox.OfType<DjSays>(), x => x.Text == "а я ж казав, що добром це не скінчиться");
    }

    [Fact]
    public void A_word_about_the_previous_game_never_reaches_the_new_one()
    {
        var h = Table(4);
        var game = (Mafia)h.Room.Game;
        var gen = game.Generation;
        var sheriff = Seat(h, "sheriff");
        PlayNight(h, OtherCivil(h, sheriff));
        PlayVote(h, new Dictionary<int, int?>());
        PlayNight(h, sheriff);
        Assert.True(h.Rematch(Villagers[1]).Ok);

        // Модель думала довго й відповіла вже після «Ще раз»: у новій партії це марення.
        game.QueueLine("а вчора ви дивно мовчали", gen);

        Assert.Equal(0, game.PendingLines);
        h.Tick();
        Assert.DoesNotContain(h.Outbox.OfType<DjSays>(), x => x.Text.Contains("а вчора ви дивно мовчали"));
    }

    [Fact]
    public void A_word_that_came_after_the_last_word_is_never_said()
    {
        var h = Table(4);
        var game = (Mafia)h.Room.Game;
        h.Leave(h.NickOf(Seat(h, "mafia")));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var said = h.Outbox.OfType<DjSays>().Count();

        game.QueueLine("а мені здається, це був не він");
        h.Tick(5);

        // Дограну партію ніхто не тикає, і слівце так і лишається в черзі — у Балачки воно не піде.
        Assert.Equal(said, h.Outbox.OfType<DjSays>().Count());
        Assert.Equal(1, game.PendingLines);
    }

    [Fact]
    public void The_last_exile_is_announced_together_with_the_winner()
    {
        var h = Table(4);
        var mafia = Seat(h, "mafia");
        var nick = h.NickOf(mafia);
        PlayNight(h, OtherCivil(h));
        To(h, "vote");
        PlayVote(h, AliveSeats(h).Where(s => s != mafia).ToDictionary(s => s, _ => (int?)mafia));

        // Партія скінчилась голосуванням — і про вигнаного Глек має сказати в тій самій репліці,
        // що й про переможця, інакше останнє вигнання так і лишилось би непроголошеним.
        var last = h.Outbox.OfType<DjSays>().Last().Text;
        Assert.Contains(nick, last);
        Assert.Contains(MafiaGlek.CivilWin, line => last.EndsWith(line));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_a_full_table_are_instant()
    {
        var h = Table(12);
        var sw = Stopwatch.StartNew();
        h.Tick(1000);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }
}
