/*
  «Скільки?» — компанійська гра на відчуття числа. Клієнт тут нічого не вирішує: усі фази, час і очки
  живуть на сервері (Impl/Skilky.cs), а модуль лише малює те, що прийшло, і шле наміри.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { round, of, phase: 'between'|'ask'|'reveal'|'done', question, unit, endsAt,
      answered: bool[], my: number|null,
      reveal: null | { answer, years, say, rows: [{ seat, value, diff, points, accuracy, bonus, fast }] },
      scores: number[], result: null | { winners, scores } }
  points = accuracy (за точність) + bonus (найближчому) + fast (швидшому за однакової відстані).
  Кадр (подія 'frame', лише разом із новиною — чиєсь число чи зміна фази; летить усій кімнаті, прихованого нема):
    { round, of, phase, endsAt, answered, scores }
  Хід: Act('answer', { value }) — число або рядок («10 000», «2,54» сервер розбере сам).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M5.2 5.5a2.8 2.8 0 1 1 3.7 2.7c-.7.3-1 .8-1 1.5v.4" fill="none" stroke="var(--accent)" stroke-width="1.9" stroke-linecap="round"/>'
    + '<circle cx="7.9" cy="13" r="1.3" fill="var(--clay)"/></svg>';

  const MS = { between: 3000, ask: 30000, bet: 10000, reveal: 6000 };

  /// Шкала очок словами — та сама, що в Skilky.Accuracy на сервері. Міняєш там — міняй і тут.
  const RULES = 'Очки за точність: до 2 % — 5, до 10 % — 4 (промах на одиницю — теж 4), до 25 % — 3, до 50 % — 2, '
    + 'до двох разів — 1. Роки: точно — 5, ±2 — 4, ±5 — 3, ±15 — 2, ±50 — 1. '
    + 'Найближчому +1, навіть коли всі мимо (самому — без цього). Однаково близько — швидшому ще +1. '
    + 'За кожні 5 очок партії — 🏺 черепок';

  /// Скільки очок партії коштує черепок — те саме, що Skilky.PointsPerShard на сервері.
  const POINTS_PER_SHARD = 5;

  /// Число для ока: ціле — з пробілами між тисячами, дробове — без хвоста нулів.
  function num(v) {
    if (v == null || isNaN(v)) return '—';
    if (Math.abs(v - Math.round(v)) < 1e-9) return Math.round(v).toLocaleString('uk-UA');
    // Дробове теж українською: людина набирала «2,5», і крапка поруч із «1 000 000» ріже око.
    return v.toLocaleString('uk-UA', { maximumFractionDigits: 3 });
  }

  /// Рік пишемо як рік — «1986», а не «1 986»: розділювач тисяч у році ріже око.
  function yearOr(years, v) {
    return years && v != null && Math.abs(v - Math.round(v)) < 1e-9 ? String(Math.round(v)) : num(v);
  }

  /// Одиниці в банку записані формою «багато» («років», «хвилин»): з 72 чи 1 це ріже вухо. Для найчастіших —
  /// форми «один» і «два–чотири»; решта, дробові числа й одиниці з «млн»/«тис.» лишаються як є.
  const UNIT_FORMS = {
    'років': ['рік', 'роки'], 'хвилин': ['хвилина', 'хвилини'], 'днів': ['день', 'дні'], 'секунд': ['секунда', 'секунди'],
    'разів': ['раз', 'рази'], 'годин': ['година', 'години'], 'місяців': ['місяць', 'місяці'], 'тижнів': ['тиждень', 'тижні'],
    'людей': ['людина', 'людини'], 'літер': ['літера', 'літери'], 'символів': ['символ', 'символи'], 'серій': ['серія', 'серії'],
    'зубів': ['зуб', 'зуби'], 'країн': ['країна', 'країни'], 'кісток': ['кістка', 'кістки'], 'альбомів': ['альбом', 'альбоми'],
    'гравців': ['гравець', 'гравці'], 'треків': ['трек', 'треки'], 'слів': ['слово', 'слова'], 'доларів': ['долар', 'долари'],
    'гривень': ['гривня', 'гривні'], 'діб': ['доба', 'доби'], 'цифр': ['цифра', 'цифри'], 'клітинок': ['клітинка', 'клітинки'],
    'очок': ['очко', 'очки'], 'ударів': ['удар', 'удари'], 'струн': ['струна', 'струни'], 'рядків': ['рядок', 'рядки'],
    'хромосом': ['хромосома', 'хромосоми'], 'хребців': ['хребець', 'хребці'], 'ніг': ['нога', 'ноги'], 'станцій': ['станція', 'станції'],
    'пісень': ['пісня', 'пісні'], 'фільмів': ['фільм', 'фільми'], 'світлових років': ['світловий рік', 'світлові роки'],
    'вершин': ['вершина', 'вершини'], 'планет': ['планета', 'планети'], 'кольорів': ['колір', 'кольори'], 'медалей': ['медаль', 'медалі'],
  };

  function unitFor(n, unit) {
    const forms = UNIT_FORMS[unit];
    if (!forms || n == null || Math.abs(n - Math.round(n)) > 1e-9) return unit;
    const k = Math.abs(Math.round(n));
    const d = k % 10, h = k % 100;
    if (d === 1 && h !== 11) return forms[0];
    if (d >= 2 && d <= 4 && (h < 12 || h > 14)) return forms[1];
    return unit;
  }

  function state(root) {
    if (!root._sk) root._sk = { round: -1 };
    return root._sk;
  }

  /// innerHTML лише коли HTML справді інший. Порівнюємо з тим, що самі клали, а не з el.innerHTML: браузер
  /// серіалізує по-своєму (апостроф з esc — «&#39;» проти «'»), і рівність не наставала ніколи — вузли
  /// перебудовувались на кожен кадр, а анімації розкриття починались спочатку.
  function setHtml(el, html) {
    if (el._h === html) return;
    el._h = html;
    el.innerHTML = html;
  }

  /// Те саме читання числа, що й на сервері (Skilky.Text): пробіли геть, кома — крапка. null — не число.
  function parseNum(raw) {
    const clean = String(raw || '').replace(/\s+/g, '').replace(/,/g, '.');
    if (!clean || !/^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i.test(clean)) return null;
    const n = Number(clean);
    return Number.isFinite(n) ? n : null;
  }

  /// Підказка під полем, поки людина набирає: «13800000000» на телефоні не прочитати, «= 13 800 000 000» —
  /// одразу видно, чи не загубився нуль. Для малих чисел мовчить: «42» і так видно.
  function paintPreview(root) {
    const input = root.querySelector('.skin');
    const box = root.querySelector('.skprev');
    if (!input || !box) return;
    const raw = (input.value || '').trim();
    const n = raw ? parseNum(raw) : null;
    // Уже надіслане число й так стоїть рядком нижче («Твоє число: …») — двічі не пишемо.
    const sent = root._sk && root._sk.my;
    const text = !raw || input.disabled || (n != null && sent != null && Math.abs(n - sent) < 1e-9) ? ''
      : n == null ? 'Не схоже на число — лише цифри, пробіли й кома'
      : Math.abs(n) >= 10000 || Math.abs(n - Math.round(n)) > 1e-9 ? '= ' + num(n)
      : '';
    if (box.textContent !== text) box.textContent = text;
    box.classList.toggle('bad', !!raw && n == null);
  }

  /// Кадр свіжіший за вид (він летить щосекунди), але вірити йому можна лише в межах того самого раунду
  /// й фази: після «Ще раз» ctx.frame ще секунду тримає останній кадр минулої партії — з чужим рахунком
  /// і зі старою розсадкою.
  function fresh(ctx, v) {
    const f = ctx.frame;
    return f && f.round === v.round && f.phase === v.phase ? f : null;
  }

  /// Чиї числа вже прийшли.
  function ticks(ctx, v) {
    const f = fresh(ctx, v);
    return (f && f.answered) || v.answered || [];
  }

  /// Місця, на яких хтось сидить. Порожні (стіл на дванадцятьох, грає четверо) не малюємо взагалі.
  function seats(ctx) {
    const room = ctx.room || {};
    const list = [];
    const n = (room.seats && room.seats.length) || 0;
    for (let i = 0; i < n; i++) if (ctx.nickOf(i)) list.push(i);
    return list;
  }

  function paintWho(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.skwho');
    if (!box) return;
    const done = ticks(ctx, v);
    // У лобі склад столу вже видно в шапці картки — другий раз його малювати нема чого.
    const show = ctx.playing && (v.phase === 'ask' || v.phase === 'between' || v.phase === 'bet') && !v.teams;
    const html = show ? seats(ctx).map((i) => '<span class="skchip' + (done[i] ? ' on' : '') + '">'
      + (done[i] ? '✓ ' : '') + ctx.esc(ctx.nickOf(i)) + '</span>').join('') : '';
    setHtml(box, html);
  }

  function paintScores(root, ctx) {
    const v = ctx.view || {};
    const f = fresh(ctx, v);
    const sc = (f && f.scores && f.scores.length) ? f.scores : (v.scores || []);
    const box = root.querySelector('.skscore');
    if (!box) return;
    const win = (v.result && v.result.winners) || [];
    const list = seats(ctx);
    // Рахунок з'являється, щойно в когось є очки: у лобі й до першого розкриття всі нулі нікому нічого не
    // кажуть, а на телефоні дванадцять рядків нулів штовхали вниз усе інше.
    const html = (!ctx.playing && !v.result) || (!v.result && !list.some((i) => sc[i])) ? '' : list
      .slice()
      .sort((a, b) => (sc[b] || 0) - (sc[a] || 0) || a - b)
      .map((i) => {
        // Після кінця партії поруч із рахунком — скільки черепків він приніс (сервер платить так само).
        const shards = v.result ? Math.floor((sc[i] || 0) / POINTS_PER_SHARD) : 0;
        return '<div class="sksrow' + (win.includes(i) ? ' win' : '') + '">'
          + '<span>' + ctx.esc(ctx.nickOf(i)) + '</span>'
          + (shards > 0 ? '<i class="skshard" title="черепки за очки">🏺+' + shards + '</i>' : '')
          + '<b>' + (sc[i] || 0) + '</b></div>';
      }).join('');
    setHtml(box, html);
  }

  /// «Рази» після числа: дробове — «2,5 раза», ціле — «3 рази», «5 разів».
  function times(k) {
    // Від десяти разів десяті частки вже шум: «у 1 804,8 раза» читається гірше за «у 1 805 разів».
    const r = k >= 10 ? Math.round(k) : Math.round(k * 10) / 10;
    if (r !== Math.round(r)) return num(r) + ' раза';
    const n = Math.round(r) % 100;
    const word = n % 10 >= 2 && n % 10 <= 4 && (n < 12 || n > 14) ? 'рази' : 'разів';
    return num(Math.round(r)) + ' ' + word;
  }

  /// Як сильно повз — тими ж мірками, якими сервер дає очки: роки в роках, решта у відсотках, а
  /// далеко за межами — у разах (там відсотки вже нічого не кажуть: «на 900 % більше»).
  function missText(r, x) {
    if (Math.abs(x.diff) < 1e-9) return 'точно';
    if (r.years || !(r.answer > 0) || !(x.value > 0)) return 'різниця ' + num(x.diff);
    const more = x.value > r.answer;
    const ratio = more ? x.value / r.answer : r.answer / x.value;
    if (ratio >= 2) return 'у ' + times(ratio) + (more ? ' більше' : ' менше');
    const pct = x.diff / r.answer * 100;
    return 'на ' + num(pct < 10 ? Math.round(pct * 10) / 10 : Math.round(pct)) + ' %' + (more ? ' більше' : ' менше');
  }

  function revealHtml(ctx, v) {
    const r = v.reveal;
    if (!r) return '';
    const rows = r.rows || [];
    const head = '<div class="skans"><span class="muted small">Правильна відповідь</span>'
      + '<b>' + yearOr(r.years, r.answer) + '</b>' + (v.unit ? '<i>' + ctx.esc(unitFor(r.answer, v.unit)) + '</i>' : '') + '</div>';
    // Слово Глека про раунд — тут, під таблицею, а не в загальних Балачках (там за вечір їх були сотні).
    const say = r.say ? '<div class="sksay"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(r.say) + '</span></div>' : '';
    if (!rows.length) return head + '<div class="gempty">Отакої — ніхто не назвав жодного числа.</div>' + say;
    return head + '<div class="skrows">' + rows.map((x, n) => {
      const bonus = x.bonus || 0;
      const fast = x.fast || 0;
      const acc = x.accuracy != null ? x.accuracy : x.points - bonus - fast;
      // Розклад суми показуємо лише тоді, коли є бонуси: «+5» без «5» під ним, але «+6» із «5+1».
      const parts = [acc].concat(bonus ? [bonus] : [], fast ? [fast] : []);
      const why = [acc ? acc + ' за точність' : '', bonus ? bonus + ' найближчому' : '', fast ? fast + ' за швидкість' : '']
        .filter(Boolean).join(', ');
      // --n — порядковий номер рядка: рядки випливають по черзі, від найближчого (skilky.css).
      return '<div class="skrow' + (x.points ? ' on' : '') + (bonus ? ' best' : '') + '" style="--n:' + n + '">'
        + '<span class="skn">' + (bonus ? '🏆 ' : '') + (fast ? '⚡ ' : '')
        + ctx.esc(whoOf(ctx, v, x)) + '</span>'
        + '<span class="skv">' + yearOr(r.years, x.value) + '</span>'
        + '<span class="skd muted small">' + missText(r, x) + '</span>'
        + '<span class="skp"' + (why ? ' title="' + why + '"' : '') + '>'
        + (x.points ? '+' + x.points : '0')
        + (parts.length > 1 ? '<small>' + parts.join('+') + '</small>' : '')
        + '</span></div>';
    }).join('') + '</div>' + say;
  }

  /// Підсумок партії: усі запитання, правда й хто був найближче. Наприкінці хочеться не лише рахунку,
  /// а й «а пам'ятаєш, як ми з Маттергорном?».
  function recapHtml(ctx, v) {
    const list = v.recap || [];
    if (!list.length) return '';
    return '<details class="skrecap" open><summary>Як це було · ' + list.length + ' ' + plural(list.length, 'питання', 'питання', 'питань') + '</summary><ol>'
      + list.map((r) => {
        const who = (r.best || []).map((i) => ctx.esc(ctx.nickOf(i) || ctx.seatName(i))).join(', ');
        const guess = r.value == null ? '<span class="muted">ніхто не відповів</span>'
          : '🎯 ' + who + ' — ' + yearOr(r.years, r.value) + (r.points ? ' <b>+' + r.points + '</b>' : '');
        return '<li><span class="skrq">' + ctx.esc(r.question) + '</span>'
          + '<span class="skra"><b>' + yearOr(r.years, r.answer) + '</b>' + (r.unit ? ' ' + ctx.esc(unitFor(r.answer, r.unit)) : '') + '</span>'
          + '<span class="skrb small">' + guess + '</span></li>';
      }).join('') + '</ol></details>';
  }

  function plural(n, one, few, many) {
    const d = n % 10, h = n % 100;
    return d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many;
  }

  const PLACE = ['перше', 'друге', 'третє', 'четверте', 'п’яте', 'шосте', 'сьоме', 'восьме', 'дев’яте', 'десяте', 'одинадцяте', 'дванадцяте'];
  const pts = (n) => n + ' ' + plural(Math.abs(n), 'очко', 'очки', 'очок');
  const shardsOf = (n) => Math.floor((n || 0) / POINTS_PER_SHARD);

  /// Підсумок угорі картки, коли партію зіграно: хто виграв — першим рядком, нижче — твоє місце. Раніше тут
  /// лишалась таблиця останнього розкриття, і її 🏆 («найближчий у цьому питанні») плутали з переможцем партії;
  /// саме те питання й так є в «Як це було».
  function podiumHtml(ctx, v) {
    if (v.phase !== 'done' || !v.result) return '';
    const sc = v.scores || [];
    const list = seats(ctx);
    if (!list.length) return '';
    const bonus = (n) => (shardsOf(n) > 0 ? ' <span class="skshard">🏺+' + shardsOf(n) + '</span>' : '');
    if (list.length === 1) {
      const i = list[0], n = sc[i] || 0;
      return '<div class="skpodt">🎯 ' + (i === ctx.seat ? 'Твій результат' : ctx.esc(ctx.nickOf(i))) + ': <b>' + pts(n) + '</b>' + bonus(n) + '</div>';
    }
    const win = v.result.winners || [];
    if (!win.length) return '<div class="skpodt">🤷 Ніхто нічого не вгадав — нічия</div>';
    let html = '<div class="skpodt">🏆 ' + win.map((i) => '<b>' + ctx.esc(ctx.nickOf(i) || ctx.seatName(i)) + '</b>').join(' і ')
      + ' — ' + pts(sc[win[0]] || 0) + '</div>';
    if (ctx.mine && list.includes(ctx.seat)) {
      const my = sc[ctx.seat] || 0;
      const place = 1 + list.filter((i) => (sc[i] || 0) > my).length;
      const where = win.includes(ctx.seat)
        ? (win.length > 1 ? 'Ти серед переможців!' : 'Це ти — перше місце з ' + list.length + '!')
        : 'Ти — ' + (PLACE[place - 1] || place + '-е') + ' місце з ' + list.length + ' · ' + pts(my);
      html += '<div class="skpods muted">' + where + bonus(my) + '</div>';
    }
    return html;
  }

  function answer(root, ctx) {
    const input = root.querySelector('.skin');
    if (!input) return;
    const raw = (input.value || '').trim();
    if (!raw) { ctx.toast('Ану, напиши число', 'err'); input.focus(); return; }
    // Шлемо рядком: «10 000» і «2,54» сервер прочитає сам, а число з input.value і так було б рядком.
    Promise.resolve(ctx.act('answer', { value: raw })).then((r) => {
      // На телефоні після відповіді клавіатура ховається: вона закривала пів екрана — і галочки, хто вже
      // відповів, і розкриття. Передумав — тапни в поле ще раз.
      if (r && r.ok && HGames.ui.coarse() && document.activeElement === input) input.blur();
    }, () => {});
  }


  // ---------------------------------------------------------------- прохід №3 (29.09)

  const TEAM_CLASS = ['skt0', 'skt1', 'skt2', 'skt3'];

  /// Хто це в рядку: у командах — назва команди, інакше нік.
  function whoOf(ctx, v, x) {
    if (x.team != null && x.team >= 0 && v.teams && v.teams[x.team]) return v.teams[x.team].name;
    return ctx.nickOf(x.seat) || ctx.seatName(x.seat);
  }

  /// Числова пряма (№42): усі числа крапками, правда — прапорцем. Роки — лінійно, решта — логарифмом, коли
  /// числа розкидані на порядки: тоді чиєсь «у 38 000 разів більше» видно, наскільки воно далеко.
  function lineHtml(ctx, v) {
    const r = v.reveal;
    const rows = (r && r.rows) || [];
    if (!rows.length) return '';
    const vals = rows.map((x) => x.value).concat([r.answer]);
    const lo = Math.min.apply(null, vals), hi = Math.max.apply(null, vals);
    if (!(hi > lo)) return '';
    const log = !r.years && lo > 0 && hi / lo > 4;
    const f = (x) => (log ? Math.log10(x) : x);
    const a = f(lo), b = f(hi);
    const pos = (x) => 3 + 94 * (f(x) - a) / (b - a);
    const narrow = rows.length > 5;
    const lanes = [-1e9, -1e9, -1e9];
    const dots = rows.map((x, n) => ({ x, n, p: pos(x.value) })).sort((p, q) => p.p - q.p).map((d) => {
      let lane = lanes.findIndex((l) => d.p - l > (narrow ? 7 : 13));
      if (lane < 0) lane = lanes.indexOf(Math.min.apply(null, lanes));
      lanes[lane] = d.p;
      const who = whoOf(ctx, v, d.x);
      const label = narrow ? String(d.n + 1) : who;
      return '<span class="skdot' + (d.x.bonus ? ' best' : '') + '" style="left:' + d.p.toFixed(1) + '%;--l:' + lane + '" title="'
        + ctx.esc(who + ': ' + yearOr(r.years, d.x.value)) + '"><i></i><b>' + ctx.esc(label) + '</b></span>';
    }).join('');
    const flag = '<span class="skflag" style="left:' + pos(r.answer).toFixed(1) + '%" title="Правда: ' + yearOr(r.years, r.answer) + '">🚩</span>';
    return '<div class="skline" aria-hidden="true"><div class="skaxis">' + dots + flag + '</div>'
      + '<div class="skends muted small"><span>' + yearOr(r.years, lo) + '</span>'
      + (log ? '<span>шкала в разах</span>' : '') + '<span>' + yearOr(r.years, hi) + '</span></div></div>';
  }

  /// Ставки розкритого раунду: хто на кого поставив і чи вгадав.
  function betsHtml(ctx, v) {
    const list = (v.reveal && v.reveal.bets) || [];
    if (!list.length) return '';
    return '<div class="skbets small"><b>🎲 Ставки:</b> ' + list.map((b) => '<span class="' + (b.ok ? 'ok' : 'no') + '">'
      + ctx.esc(ctx.nickOf(b.seat) || ctx.seatName(b.seat)) + ' → ' + ctx.esc(ctx.nickOf(b.on) || ctx.seatName(b.on))
      + (b.ok ? ' ✓ +2' : ' ✗') + '</span>').join(' ') + '</div>';
  }

  /// Фаза ставок (№41): числа вже на столі, правди ще нема — тисни, чиє найближче.
  function betHtml(ctx, v) {
    const bet = v.bet;
    if (!bet) return '';
    const can = ctx.mine && ctx.playing;
    const unit = v.unit === 'рік';
    return '<div class="skbetq">🎲 Чиє число найближче до правди? Вгадаєш — <b>+2</b></div><div class="skbetl">'
      + bet.values.map((x) => {
        const own = x.seat === ctx.seat;
        const on = bet.on === x.seat;
        return '<button type="button" class="skbetb' + (on ? ' on' : '') + '" data-bet="' + x.seat + '"'
          + (!can || own ? ' disabled' : '') + '><span>' + ctx.esc(ctx.nickOf(x.seat) || ctx.seatName(x.seat)) + (own ? ' (ти)' : '')
          + '</span><b>' + yearOr(unit, x.value) + '</b></button>';
      }).join('') + '</div>';
  }

  /// Своя команда під час відповіді (№47): пропозиції, 👍/👎 і «Подати» для капітана.
  function teamHtml(ctx, v) {
    const t = v.team;
    if (!t || !v.teams) return '';
    const team = v.teams[t.t] || {};
    const cap = t.captain === ctx.seat;
    const unit = v.unit === 'рік';
    const head = '<div class="skteamh ' + TEAM_CLASS[t.t] + '"><b>' + ctx.esc(team.name || '') + '</b> · капітан: '
      + ctx.esc(ctx.nickOf(t.captain) || '—') + (cap ? ' <i>(ти — подаєш число команди)</i>' : '') + '</div>';
    const fin = t.final != null ? '<div class="skteamf">✅ Подано від команди: <b>' + yearOr(unit, t.final) + '</b></div>' : '';
    const rows = (t.drafts || []).map((d) => {
      const own = d.seat === ctx.seat;
      return '<div class="skdraft"><span>' + ctx.esc(ctx.nickOf(d.seat) || '') + '</span><b>' + yearOr(unit, d.value) + '</b>'
        + '<span class="skvotes">' + (own ? '<i>👍 ' + d.up + ' · 👎 ' + d.down + '</i>'
          : '<button type="button" class="ghost' + (d.mine > 0 ? ' on' : '') + '" data-vote="' + d.seat + '" data-up="1">👍 ' + d.up + '</button>'
          + '<button type="button" class="ghost' + (d.mine < 0 ? ' on' : '') + '" data-vote="' + d.seat + '" data-up="0">👎 ' + d.down + '</button>')
        + (cap && !own ? '<button type="button" class="primary" data-take="' + d.value + '">Подати</button>' : '') + '</span></div>';
    }).join('');
    const hint = cap ? '' : '<div class="muted small">Пиши своє — це пропозиція. Подає капітан; мовчить — піде найвподобаніша 👍</div>';
    return head + fin + rows + hint;
  }

  /// Команди в лобі й у паузі: хто з ким.
  function teamsLine(ctx, v) {
    if (!v.teams) return '';
    return '<div class="skteams small">' + v.teams.map((t, i) => '<span class="' + TEAM_CLASS[i] + '"><b>' + ctx.esc(t.name) + '</b> '
      + t.seats.map((s) => ctx.esc(ctx.nickOf(s) || '')).join(', ') + '</span>').join('') + '</div>';
  }

  /// «Питання про нас» (№44): форма в лобі й між партіями.
  function oursHtml(ctx, v) {
    const o = v.ours || {};
    const who = (o.seats || []).map((s) => ctx.esc(ctx.nickOf(s) || '')).filter(Boolean);
    const others = who.length ? '<div class="muted small">📝 Своє питання вже дописали: ' + who.join(', ') + '</div>' : '';
    if (!ctx.mine) return others;
    if (o.mine) {
      return '<div class="skoursme small">📝 Твоє питання чекає: «' + ctx.esc(o.mine.q) + '» — ' + num(o.mine.a)
        + (o.mine.unit ? ' ' + ctx.esc(o.mine.unit) : '') + ' <button type="button" class="ghost" data-ours-del>Прибрати</button></div>' + others;
    }
    return '<details class="skoursf"><summary>📝 Дописати своє питання про нас</summary>'
      + '<div class="muted small">Про компанію: «Скільки км Влад проїхав на велику?» Відповідь знаєш лише ти — ти й не відповідаєш.</div>'
      + '<input class="skoq" maxlength="160" placeholder="Скільки…?" aria-label="Питання">'
      + '<div class="skoarow"><input class="skoa" inputmode="decimal" placeholder="відповідь (число)" aria-label="Відповідь">'
      + '<input class="skou" maxlength="24" placeholder="одиниця: км, разів, рік" aria-label="Одиниця">'
      + '<button type="button" class="primary" data-ours-save>Записати</button></div></details>' + others;
  }

  /// Підсумок «Скільки? дня» (№48): таблиця дня й рядок «поділитись».
  function dailyHtml(ctx, v) {
    const d = v.daily;
    if (!d) return '';
    if (v.phase !== 'done' || !d.board) {
      return '<div class="skdayh">☀ Скільки? дня №' + d.no + ' — п’ять питань, однакових для всіх, одна спроба'
        + (d.players ? ' · сьогодні вже зіграли: ' + d.players : '') + '</div>';
    }
    const rows = d.board.map((r, i) => '<div class="skdayr' + (d.place === i + 1 ? ' me' : '') + '"><span>' + (i + 1) + '.</span><span>'
      + ctx.esc(r.nick) + '</span><span class="skdaym">' + ctx.esc(r.marks) + '</span><b>' + r.points + '</b></div>').join('');
    return '<div class="skdayh">☀ Таблиця дня №' + d.no + (d.place ? ' · ти ' + d.place + '-й з ' + d.players : '') + '</div>'
      + '<div class="skdayt">' + rows + '</div>'
      + (d.share ? '<button type="button" class="ghost" data-share>📋 Скопіювати результат для Балачок</button>' : '')
      + '<div class="muted small">Нові питання — завтра опівночі</div>';
  }

  function paintNew(root, ctx) {
    const v = ctx.view || {};
    const phase = v.phase || 'between';
    // Фото «Якого року?» — src міняємо лише коли адреса інша (інакше кожен вид перезавантажував би картинку).
    const ph = root.querySelector('.skphoto');
    const img = ph.querySelector('img');
    const src = v.photo && phase !== 'between' ? v.photo : '';
    if (img.getAttribute('src') !== src) { if (src) img.setAttribute('src', src); else img.removeAttribute('src'); }
    ph.hidden = !src;
    const c = v.credit;
    setHtml(ph.querySelector('.skcredit'), c ? '<b>' + ctx.esc(c.caption) + '</b><span class="muted">Фото: ' + ctx.esc(c.author) + ' · '
      + ctx.esc(c.license) + ' · <a href="' + ctx.esc(c.page) + '" target="_blank" rel="noopener">Вікісховище</a></span>' : '');

    const by = root.querySelector('.skby');
    const byText = v.by != null && phase !== 'between' ? (v.by === ctx.seat && ctx.mine
      ? '📝 Це твоє питання — ти мовчиш, дивись, як мучаються інші 😉' : '📝 Питання від ' + (ctx.nickOf(v.by) || 'когось із нас')) : '';
    if (by.textContent !== byText) by.textContent = byText;
    by.hidden = !byText;

    setHtml(root.querySelector('.skbet'), phase === 'bet' ? betHtml(ctx, v) : '');
    setHtml(root.querySelector('.skteam'), phase === 'ask' ? teamHtml(ctx, v) : '');
    setHtml(root.querySelector('.skteamsl'), (phase === 'between' || !ctx.playing) ? teamsLine(ctx, v) : '');
    setHtml(root.querySelector('.skextra'), phase === 'reveal' ? lineHtml(ctx, v) + betsHtml(ctx, v) : '');
    const oursOn = !v.daily && (!ctx.playing || phase === 'done');
    const ob = root.querySelector('.skours');
    // Форму не перебудовуємо, поки людина в ній пише.
    if (!ob.contains(document.activeElement)) setHtml(ob, oursOn ? oursHtml(ctx, v) : '');
    setHtml(root.querySelector('.skdaily'), dailyHtml(ctx, v));
  }

  function onClick(root, ctx, e) {
    const t = e.target.closest('button');
    if (!t || !root.contains(t)) return;
    if (t.dataset.bet != null) ctx.act('bet', { seat: +t.dataset.bet });
    else if (t.dataset.vote != null) ctx.act('vote', { seat: +t.dataset.vote, up: t.dataset.up === '1' });
    else if (t.dataset.take != null) ctx.act('answer', { value: +t.dataset.take });
    else if (t.hasAttribute('data-ours-del')) ctx.act('ours', { q: '' });
    else if (t.hasAttribute('data-ours-save')) {
      const q = (root.querySelector('.skoq') || {}).value || '';
      const a = (root.querySelector('.skoa') || {}).value || '';
      const unit = (root.querySelector('.skou') || {}).value || '';
      Promise.resolve(ctx.act('ours', { q, a, unit })).then((r) => { if (r && r.ok && document.activeElement) document.activeElement.blur(); paint(root, ctx); }, () => {});
    } else if (t.hasAttribute('data-share')) {
      const text = ((ctx.view || {}).daily || {}).share || '';
      const done = () => ctx.toast('Скопійовано — встав у Балачки');
      if (navigator.clipboard) navigator.clipboard.writeText(text).then(done, () => ctx.toast(text));
      else ctx.toast(text);
    }
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const phase = v.phase || 'between';
    const mine = ctx.mine && ctx.playing;

    const no = root.querySelector('.skno');
    const noText = !v.of ? '' : phase === 'done' ? 'Зіграно ' + v.of + ' ' + plural(v.of, 'питання', 'питання', 'питань')
      : 'Питання ' + v.round + ' з ' + v.of;
    if (no.textContent !== noText) no.textContent = noText;
    const pod = root.querySelector('.skpod');
    const podHtml = podiumHtml(ctx, v);
    setHtml(pod, podHtml);
    pod.hidden = !podHtml;

    // Дуга живе лише поки партія йде: у лобі та після кінця відліку нема.
    const top = root.querySelector('.sktop');
    if (ctx.playing && v.endsAt && phase !== 'done') {
      // Час на відповідь господар обирає сам (15–60 с), тож повну дугу для «ask» беремо з виду.
      const total = phase === 'ask' && v.seconds ? v.seconds * 1000 : (MS[phase] || 30000);
      HGames.ui.timerArc(top, v.endsAt, total);
    } else {
      const arc = top.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }

    const q = root.querySelector('.skq');
    const qText = phase === 'between' && !v.reveal && !ctx.playing ? 'Господар тисне «Почати» — можна й самому, а хто встигне, підсяде'
      : phase === 'between' ? 'Готуйсь…'
      : (v.question || '');
    if (q.textContent !== qText) q.textContent = qText;
    q.classList.toggle('wait', phase === 'between');
    // Партію зіграно — замість останнього запитання підсумок угорі (воно саме є в «Як це було»).
    q.hidden = !!podHtml;

    // Шкала очок — поки чекаємо: у лобі й у паузі перед запитанням. Під час відповіді вона лише заважає.
    const rules = root.querySelector('.skrules');
    rules.hidden = !(phase === 'between' && !v.result);

    // Нове запитання — чисте поле: чуже число з минулого раунду там висіти не має.
    const ask = root.querySelector('.skask');
    const input = root.querySelector('.skin');
    if (st.round !== v.round) { st.round = v.round; input.value = ''; }
    st.my = phase === 'ask' ? v.my : null;
    // Автор «питання про нас» на своє не відповідає.
    const canAsk = mine && phase === 'ask' && v.by !== ctx.seat;
    const opened = canAsk && ask.hidden;
    ask.hidden = !canAsk;
    input.disabled = !canAsk;
    // Запитання з'явилось — курсор одразу в полі, щоб не шукати його мишкою. На телефоні — ні: там
    // фокус підкидає клавіатуру на пів екрана ще до того, як людина дочитала запитання.
    if (opened && !HGames.ui.coarse() && !document.querySelector('.modal:not([hidden]) input:focus')) {
      const busy = document.activeElement;
      if (!busy || busy === document.body || !/^(INPUT|TEXTAREA|SELECT)$/.test(busy.tagName)) input.focus();
    }
    const unit = root.querySelector('.skunit');
    const unitText = v.unit || '';
    if (unit.textContent !== unitText) unit.textContent = unitText;
    unit.hidden = !unitText || !canAsk;
    const ph = v.team ? (v.team.captain === ctx.seat ? 'число команди' : 'пропозиція команді') : 'твоє число';
    if (input.placeholder !== ph) input.placeholder = ph;

    const my = root.querySelector('.skmy');
    const myText = v.my != null && phase === 'ask' ? 'Твоє число: ' + yearOr(v.unit === 'рік', v.my) + '. Можна змінити, поки є час'
      : !ctx.mine && ctx.playing && phase === 'ask' ? 'Дивишся збоку'
      : '';
    if (my.textContent !== myText) my.textContent = myText;

    const rev = root.querySelector('.skrev');
    const html = phase === 'reveal' || (phase === 'done' && !podHtml) ? revealHtml(ctx, v) : '';
    setHtml(rev, html);
    const recap = root.querySelector('.skrecapbox');
    const recapText = phase === 'done' ? recapHtml(ctx, v) : '';
    // Порівнюємо з тим, що малювали, а не з innerHTML: інакше кожен кадр згортав би розгорнуте людиною.
    setHtml(recap, recapText);
    paintPreview(root);

    paintWho(root, ctx);
    paintScores(root, ctx);
    paintNew(root, ctx);
  }

  // Той самий модуль малює і стіл, і «Скільки? дня» (сервер: Client = "skilky").
  const MOD = {
    id: 'skilky',
    news: {
      v: '2026-09-29',
      title: 'Скільки?: ставки, команди, питання про нас і фото',
      items: [
        '📷 Нова тема «Якого року?»: фото з Вікісховища — вгадай рік зйомки',
        '☀ «Скільки? дня»: п’ять питань, однакових для всіх, таблиця дня й рядок для Балачок',
        '🎲 «Ставлю на чуже» (опція): числа на столі без правди — вгадай, чиє найближче, +2',
        '👥 Команди 2–4 (опція) і 📝 питання про нас: допиши своє в лобі — інші вгадують',
        '📏 У розкритті — числова пряма з усіма числами й 🚩 правдою; 🏺 тема «Наше» про ігри сайту',
      ],
    },
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd'],

    mount(root, ctx) {
      root._sk = { round: -1 };
      // .skmain — усе, що стосується запитання; .skscore — рахунок партії, який на широкій картці
      // від'їжджає праворуч (skilky.css), а на телефоні лягає під таблицю.
      root.innerHTML = '<div class="skwrap">'
        + '<div class="skmain">'
        + '<div class="sktop"><span class="skno muted small"></span></div>'
        + '<div class="skpod" hidden></div>'
        + '<div class="skdaily"></div>'
        + '<div class="skby small" hidden></div>'
        + '<div class="skq"></div>'
        + '<figure class="skphoto" hidden><img alt="Фото — якого року?" decoding="async"><figcaption class="skcredit small"></figcaption></figure>'
        + '<div class="skrules muted small" hidden>' + RULES + '</div>'
        + '<div class="skask" hidden><input class="skin" type="text" inputmode="decimal" autocomplete="off"'
        + ' placeholder="твоє число" aria-label="Твоє число"><span class="skunit muted small"></span>'
        + '<button type="button" class="primary skgo">Відповісти</button></div>'
        + '<div class="skprev small" aria-live="polite"></div>'
        + '<div class="skmy muted small"></div>'
        + '<div class="skteamsl"></div>'
        + '<div class="skbet"></div>'
        + '<div class="skteam"></div>'
        + '<div class="skrev"></div>'
        + '<div class="skextra"></div>'
        + '<div class="skrecapbox"></div>'
        + '<div class="skwho"></div>'
        + '<div class="skours"></div>'
        + '</div>'
        + '<div class="skscore"></div>'
        + '</div>';
      root.querySelector('.skgo').onclick = () => answer(root, ctx);
      root.querySelector('.skin').addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        answer(root, ctx);
      });
      root.querySelector('.skin').addEventListener('input', () => paintPreview(root));
      root.querySelector('.skwrap').addEventListener('click', (e) => onClick(root, ctx, e));
      const img = root.querySelector('.skphoto img');
      img.fetchPriority = 'high';
      paint(root, ctx);
    },

    update(root, ctx) { paint(root, ctx); },

    /// Кадр не чіпає ні питання, ні розкриття — лише те, що змінюється щосекунди.
    frame(root, ctx) {
      if (!root.querySelector('.skwho')) return;
      paintWho(root, ctx);
      paintScores(root, ctx);
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // Фазу беремо з виду: сервер міняє її тільки разом із видом (TickResult.Both), тож кадр її не
      // випереджає — а от після «Ще раз» кадр ще секунду тримає фазу минулої партії.
      const s = ctx.view || {};
      if (s.phase === 'ask') return ctx.mine ? 'Пиши число й тисни Enter' : 'Гравці думають…';
      if (s.phase === 'bet') return ctx.mine ? 'Тисни, чиє число найближче: +2, якщо вгадаєш' : 'Ставки: чиє число найближче?';
      if (s.phase === 'ask' && s.by === ctx.seat && ctx.mine) return 'Твоє питання — цього разу ти лише дивишся';
      if (s.phase === 'ask' && s.team) return s.team.captain === ctx.seat ? 'Ти капітан: подай число команди' : 'Пропонуй число — подає капітан';
      if (s.phase === 'reveal') return 'Ось як було насправді';
      if (s.phase === 'between') return 'Зараз буде питання…';
      return '';
    },

    unmount(root) {
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      root._sk = null;
    },
  };
  HGames.register(MOD);
  HGames.register(Object.assign({}, MOD, {
    id: 'skilky-daily',
    added: '2026-09-29',
    news: undefined,
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="3.2" fill="var(--clay)"/>'
      + '<path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4" stroke="var(--accent)" stroke-width="1.5" stroke-linecap="round"/></svg>',
  }));
})();
