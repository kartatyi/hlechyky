// 🌅 Небо (web/sky.js): сайт живе за справжнім часом — схід, день, захід, ніч — і за справжньою погодою.
// Де сонце, рахуємо самі (дата й координати, жодних сторонніх даних); хмари, дощ і сніг дає сервер (/api/weather:
// він сам питає Open-Meteo раз на чверть години, браузери людей туди не ходять). Нема погоди — небо просто ясне.
//
// Малює все CSS (static/polyva.css) і пейзаж static/obrii.svg — Тетерів у Житомирі: небо в три кольори, далекі
// пагорби, підвісний міст, береги каньйону, вода з відблиском; усе далеке тоне в тумані, чіткий лише очерет спереду.
// Колір обрію (--tint) і бік, звідки світить сонце (--sun-x, --sh-x), беруть фон і блоки всього сайту. Звідси йдуть лише змінні на <html> — кольори
// (--sk0…--sk2, --l1…--l4 і їхні «…m» у тумані, --lb міст, --rock, --w0, --w1, --pad, --mist), скільки туману
// (--sky-mist), ліхтарі на мосту (--sky-lamp), світила (--sky-orb, --sun-c, --moon-c, --sky-halo…; місяць — зі справжньою фазою),
// доріжка світла на воді (--sky-oo, --refl-w — за фазою й розміром того, що світить), зорі й хмари
// (--sky-stars; самі хмари, дощ, сніг і птахи генеруються тут же — див. clouds(), fall() і birds()) — і два атрибути:
// data-sky (night | dawn | day | dusk) та data-wx (clear | cloud | fog | rain | snow | storm). Перераховуємо раз на
// п'ять хвилин і коли вкладка вертається; кольори не анімуються зовсім.
(function () {
  'use strict';
  let o = null;
  let wx = null;                 // остання відповідь /api/weather або null
  let wxAt = 0;
  let fake = null;               // перегляд: { at: Date | null, wx: 'rain' | … } — з адреси ?sky=06:10&wx=rain&day=01-15 (місяць-день, можна з роком: 2028-12-31) або HSky.preview()
  let pos = null;                // де зараз сонце й місяць і що з них світить — рахує compute(), розставляє place()
  const KYIV = { lat: 50.45, lon: 30.52 };
  const RAD = Math.PI / 180;
  // Пейзаж намальовано в цих одиницях (viewBox 0 90 2000 470); гребінь далеких пагорбів — на 236 одиниць вище за низ.
  const ART_W = 2000, ART_H = 470, ART_Y = 90, HORIZON = 236;

  // ---------------------------------------------------------------------------------------------
  // де сонце й місяць
  // ---------------------------------------------------------------------------------------------

  /// Сонце й місяць: висота над обрієм (градуси), часовий кут (радіани, від'ємний — до кульмінації) і top — найвища
  /// точка, куди світило сьогодні підніметься. Формули — «низької точності» з астрономічного щорічника: сонце — до
  /// сотих градуса, місяць — близько 0,3° за довготою й 0,2° за широтою (час повні — до півгодини). Цього досить і
  /// для фази, і щоб упізнати місячне затемнення.
  ///   moon.k — освітлена частка, moon.wax — росте; moon.size — видимий розмір проти середнього (у перигеї більший,
  ///   в апогеї менший, різниця до 14 %); moon.ecl — затемнення: 0 — нема, 1 — повне (місяць у тіні Землі й червоніє:
  ///   до нього доходить лише світло, заломлене земною атмосферою, — те саме, що робить червоним захід сонця).
  function sky(date, lat, lon) {
    const d = (date.getTime() - Date.UTC(2000, 0, 1, 12)) / 864e5, T = d / 36525;
    const L = (280.46 + 0.9856474 * d) % 360;
    const g = ((357.528 + 0.9856003 * d) % 360) * RAD;
    const lam = (L + 1.915 * Math.sin(g) + 0.02 * Math.sin(2 * g)) * RAD;
    const eps = (23.439 - 0.0000004 * d) * RAD;
    const gmst = (18.697374558 + 24.06570982441908 * d) % 24;
    const body = (l, b) => {
      const cb = Math.cos(b), sb = Math.sin(b);
      const ra = Math.atan2(Math.cos(eps) * cb * Math.sin(l) - Math.sin(eps) * sb, cb * Math.cos(l));
      const dec = Math.asin(Math.sin(eps) * cb * Math.sin(l) + Math.cos(eps) * sb);
      let ha = (gmst * 15 + lon) * RAD - ra;
      ha = Math.atan2(Math.sin(ha), Math.cos(ha));
      const alt = Math.asin(Math.sin(lat * RAD) * Math.sin(dec) + Math.cos(lat * RAD) * Math.cos(dec) * Math.cos(ha));
      return { alt: alt / RAD, ha, top: 90 - Math.abs(lat - dec / RAD) };
    };
    const S = (a, b) => Math.sin((a + b * T) * RAD), C = (a, b) => Math.cos((a + b * T) * RAD);
    const ml = (218.32 + 481267.881 * T + 6.29 * S(135.0, 477198.87) - 1.27 * S(259.3, -413335.36) + 0.66 * S(235.7, 890534.22) +
      0.21 * S(269.9, 954397.74) - 0.19 * S(357.5, 35999.05) - 0.11 * S(186.5, 966404.03)) * RAD;
    const mb = (5.13 * S(93.3, 483202.02) + 0.28 * S(228.2, 960400.89) - 0.28 * S(318.3, 6003.15) - 0.17 * S(217.6, -407332.21)) * RAD;
    const par = 0.9508 + 0.0518 * C(135.0, 477198.87) + 0.0095 * C(259.3, -413335.36) + 0.0078 * C(235.7, 890534.22) + 0.0028 * C(269.9, 954397.74);
    const moon = body(ml, mb);
    const cosEl = Math.cos(mb) * Math.cos(ml - lam);                       // кут між місяцем і сонцем
    moon.k = (1 - cosEl) / 2;
    moon.wax = Math.sin(ml - lam) > 0;
    moon.size = par / 0.9508;
    // затемнення: наскільки місяць зайшов у тінь Землі (її радіус на відстані місяця — близько 0,7°, радіус місяця — 0,26°)
    const sep = Math.acos(clamp(-cosEl, -1, 1)) / RAD, umbra = 1.02 * (0.9983 * par - 0.264), sd = 0.2724 * par;
    moon.ecl = clamp((umbra + sd - sep) / (2 * sd), 0, 1);
    const sun = body(lam, 0);
    sun.size = 1 + 0.017 * Math.cos(g);                                    // на початку січня Земля найближче до Сонця
    return { sun, moon };
  }

  // ---------------------------------------------------------------------------------------------
  // кольори
  // ---------------------------------------------------------------------------------------------

  const hex = (s) => [1, 3, 5].map((i) => parseInt(s.slice(i, i + 2), 16));
  const mix = (a, b, t) => a.map((x, i) => Math.round(x + (b[i] - x) * t));
  const css = (c) => 'rgb(' + c.join(' ') + ')';
  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
  // Пора року: як виглядає земля й листя опівдні взимку (іній), навесні, влітку й восени. Літо — те, з чого
  // намальовано пейзаж; решта — множник до нього, тож ніч, сутінки й погода діють так само в будь-яку пору.
  const SUMMER = hex('#3a7355');
  const LAND = ['#93a6a4', '#56894a', '#3a7355', '#7c743c'].map(hex);
  const LEAF = ['#7f8d88', '#6f9f52', '#3a7355', '#b9742a'].map(hex);
  const LEAF2 = ['#86928c', '#8fb85e', '#44804f', '#cda62a'].map(hex);   // другий і третій відтінки листяних: восени — жовтий і червоний
  const LEAF3 = ['#7a8884', '#7aa860', '#346a50', '#a5432a'].map(hex);
  const EVER = ['#4d6f66', '#3a7355', '#3a7355', '#3f6e4c'].map(hex);   // сосни зелені цілий рік, узимку — з інеєм
  const BG = hex('#0f1f18');     // наш зелений: у нього сходить передній план, тож пейзаж виростає зі сторінки
  /// Чотири пори: небо згори донизу (верх, середина, обрій), зелень землі в цьому світлі, світило, сяйво довкола, зорі.
  /// Небо тримається нашого зеленого: верх — глибокий синьо-зелений (у нього вростає шапка), обрій — теплий, до
  /// нашого жовтого. mist — скільки туману лежить між шарами (уранці й увечері більше, опівдні менше, але є завжди).
  const PAL = {
    night: { sk: ['#06100f', '#0c1d24', '#1d3a3f'], g: '#0d1c16', orb: '#ece8d6', halo: 0.16, stars: 1, mist: 0.5 },
    dawn: { sk: ['#1c3048', '#7d7391', '#eab98c'], g: '#24443a', orb: '#ffe2b4', halo: 0.5, stars: 0.15, mist: 1 },
    day: { sk: ['#2b7d9c', '#63b3c2', '#b7e0d2'], g: '#3a7355', orb: '#fff7d6', halo: 0.42, stars: 0, mist: 0.7 },
    dusk: { sk: ['#14243f', '#7a4a6a', '#e99a4c'], g: '#203a30', orb: '#ffc37a', halo: 0.55, stars: 0.1, mist: 0.9 },
  };
  const P = {};
  for (const k of Object.keys(PAL)) P[k] = { sk: PAL[k].sk.map(hex), g: hex(PAL[k].g), orb: hex(PAL[k].orb), halo: PAL[k].halo, stars: PAL[k].stars, mist: PAL[k].mist };
  const blend = (a, b, t) => ({
    sk: a.sk.map((c, i) => mix(c, b.sk[i], t)), g: mix(a.g, b.g, t), orb: mix(a.orb, b.orb, t),
    halo: a.halo + (b.halo - a.halo) * t, stars: a.stars + (b.stars - a.stars) * t, mist: a.mist + (b.mist - a.mist) * t,
  });

  /// Погода з коду WMO (так її віддає Open-Meteo): що малювати й як назвати.
  function kind(code) {
    if (code == null) return 'clear';
    if (code >= 95) return 'storm';
    if ((code >= 71 && code <= 77) || code === 85 || code === 86) return 'snow';
    if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) return 'rain';
    if (code === 45 || code === 48) return 'fog';
    if (code >= 2) return 'cloud';
    return 'clear';
  }
  const WORD = { clear: 'ясно', cloud: 'хмарно', fog: 'туман', rain: 'дощ', snow: 'сніг', storm: 'гроза' };
  function word(code, k) {
    if (code === 1 || code === 2) return 'мінлива хмарність';
    if (code >= 51 && code <= 57) return 'мряка';
    if (code === 65 || code === 82) return 'злива';
    return WORD[k];
  }

  function compute() {
    const at = (fake && fake.at) || new Date();
    const p = (wx && wx.lat != null) ? wx : KYIV;
    const sm = sky(at, p.lat, p.lon), s = sm.sun, m = sm.moon;
    const morning = s.ha < 0;
    // −10° і нижче — ніч; до +6° — сутінки (уранці персикові, увечері жовтогарячі з бузком); вище — день.
    const t = clamp((s.alt + 10) / 16, 0, 1);
    const tw = morning ? P.dawn : P.dusk;
    const c = t < 0.5 ? blend(P.night, tw, t * 2) : blend(tw, P.day, (t - 0.5) * 2);
    const phase = t <= 0.12 ? 'night' : t >= 0.92 ? 'day' : morning ? 'dawn' : 'dusk';
    // Погода: хмарність тягне небо й землю до сірого, дощ і сніг — ще трохи.
    const k = fake && fake.wx ? fake.wx : wx ? kind(wx.code) : 'clear';
    const wet = k === 'rain' || k === 'snow' || k === 'storm' || k === 'fog';
    const cloud = fake && fake.cover != null ? fake.cover : fake && fake.wx ? (fake.wx === 'clear' ? 0 : 90) : wx && wx.cover != null ? wx.cover : k === 'clear' ? 0 : 70;
    const cover = clamp(cloud / 100, 0, 1) * (wet ? 0.86 : 0.62);
    const grey = [mix(hex('#141a22'), hex('#59646e'), t), mix(hex('#1c232b'), hex('#7f8b92'), t), mix(hex('#262e36'), hex('#a9b3b6'), t)];
    // Кольори неба лишаємо повними (схід і захід мають бути гарні); крапля нашого зеленого лише в'яже їх зі сторінкою.
    const dim = 0.05;
    // У нас темна тема: небо притемнюємо множенням (колір лишається насиченим, а не сіріє) — удень найбільше.
    const lum = 0.9 - 0.2 * t;
    const sk = c.sk.map((x, i) => mix(mix(x, grey[i], cover), BG, dim).map((v) => Math.round(v * lum)));
    const g0 = mix(c.g, mix(c.g, hex('#222b2a'), 0.5), cover);
    // Пора року йде плавно: середина зими — 15 січня, весни — 15 квітня, літа — 15 липня, осені — 15 жовтня.
    const mo = at.getMonth() + at.getDate() / 31;
    const sw = [0.5, 3.5, 6.5, 9.5].map((c0) => Math.max(0, Math.cos(((mo - c0) * Math.PI) / 6)));
    const swSum = sw[0] + sw[1] + sw[2] + sw[3];
    const [wWin, wSpr, wSum, wAut] = sw.map((x) => x / swSum);
    const season = (cols) => cols[0].map((_, i) => cols[0][i] * wWin + cols[1][i] * wSpr + cols[2][i] * wSum + cols[3][i] * wAut);
    const tone = (base, col) => base.map((x, i) => clamp(Math.round((x * col[i]) / SUMMER[i]), 0, 255));
    const g = tone(g0, season(LAND)), gE = tone(g0, season(EVER));
    const ice = clamp((wWin - 0.45) * 2.2, 0, 1);            // серед зими річка під льодом — світліша й без блиску
    const l4 = mix(g, BG, 0.74);
    // Туман — колір обрію, розбілений; у «туман» за погодою його вдвічі більше.
    const mist = mix(sk[2], hex('#ffffff'), 0.1 + 0.1 * t);

    const l1 = mix(sk[2], g, 0.24), l2 = mix(sk[2], g, 0.54), l2b = mix(sk[2], g, 0.76), l3 = mix(mix(g, BG, 0.34), sk[2], 0.1);
    const l3e = mix(mix(gE, BG, 0.34), sk[2], 0.1);
    // Вибраний вигляд («усе разом»): далеке лишається в кольорах неба й тумані, а ближній план — темні силуети нашого
    // зеленого. Від пори року й світла в них лишається відтінок (seasonal), щоб осінь і зима читались і тут.
    const near = (green, seasonal, hint) => mix(mix(sk[1], BG, green), seasonal, hint);
    const l2bN = near(0.74, l2b, 0.3), l3N = near(0.88, l3, 0.22), l3tN = near(0.84, mix(l3e, sk[2], 0.3), 0.26);
    const leafN = (pal) => near(0.66, mix(sk[2], tone(g0, season(pal)), 0.76), 0.2 + 0.5 * (wAut + wSpr * 0.5));   // улітку крони майже як ліс, восени й навесні — кольорові
    const rock2N = near(0.62, mix(l2b, mix(sk[2], hex('#b9a89a'), 0.5), 0.34), 0.08);

    // Що світить: удень сонце; вночі місяць, якщо він над обрієм (і то сильніше, що повніший); буває й безмісячна ніч.
    const day = s.alt > -4, moonUp = m.alt > 0;
    const lit = day ? s : moonUp ? m : null;
    const litK = day ? 1 : moonUp ? (0.25 + 0.75 * m.k) * (1 - 0.75 * m.ecl) : 0;     // у затемнення місяць майже не світить
    pos = { sun: s, moon: m, lit, sumTop: 90 - Math.abs(p.lat - 23.44) };
    // Колір: узимку сонце блідіше, біля самого обрію — червоніше (світло йде крізь товщу повітря, синє розсіюється).
    // Місяць низько над обрієм рудуватий з тієї ж причини, а в затемнення — мідно-червоний.
    const low = (b) => clamp(1 - b.alt / 9, 0, 1);
    const sunC = mix(mix(mix(c.orb, hex('#eef3ff'), 0.3 * wWin), hex('#ff7a3d'), low(s) * 0.4), sk[2], 0.1);
    const moonC = mix(mix(mix(hex('#ece8d6'), hex('#ffd0a0'), low(m) * 0.6), hex('#a23a22'), m.ecl * 0.85), sk[2], 0.08);
    // Розмір: місяць у перигеї на 14 % більший, ніж в апогеї, сонце в січні на 3 % більше, ніж у липні. Оком таке ледь
    // помітно, тож показуємо перебільшено (місяць — у 2,6 раза, сонце — утричі): «супермісяць» має бути видно. А біля
    // обрію обоє здаються більшими (це вже обман зору, але він є в кожного, хто дивиться на схід місяця).
    const sizeOf = (b, k) => (1 + (b.size - 1) * k) * (1 + 0.35 * low(b));
    const orbC = day ? sunC : moonC;
    const clearK = clamp(1 - cover * 1.05, 0, 1);
    const sinE = lit ? Math.sin(lit.ha) : 0;                  // де світило: −1 — ліворуч, +1 — праворуч
    return {
      phase, kind: k, cloud, code: fake && fake.wx ? null : wx ? wx.code : null,
      cl: mix(sk[2], hex('#ffffff'), 0.08 + 0.24 * t), cl2: mix(sk[1], hex('#0d1318'), wet ? 0.5 : 0.22),
      // e — попелясте світло: темний бік місяця ледь видно вночі при вузькому серпі, при половині вже майже ні
      moon: { k: m.k, wax: m.wax, ha: m.ha, s: sizeOf(m, 2.6), e: +(0.16 * (1 - m.k) ** 2 * clamp(1 - t * 1.4, 0, 1)).toFixed(2) }, moonRgb: moonC,
      // птахам: пори року (зима, весна, літо, осінь) і десятихвилинка — за десять хвилин у небі вже інші
      birds: { w: [wWin, wSpr, wSum, wAut].map((x) => +x.toFixed(2)), slot: Math.floor(at.getTime() / 600000) },
      vars: {
        '--sk0': css(sk[0]), '--sk1': css(sk[1]), '--sk2': css(sk[2]),
        // далі від нас — ближче до кольору обрію (повітряна димка), ближче — до нашого зеленого; низ кожного шару
        // тоне в тумані (…m), тож чітких країв у далечі нема
        '--l1': css(l1), '--l1m': css(mix(l1, mist, 0.55)), '--lb': css(mix(sk[2], g, 0.62)),
        '--l2': css(l2), '--l2m': css(mix(l2, mist, 0.5)),
        // граніт: трохи тепліший і світліший за берег, але в тому ж світлі (уночі не світиться)
        '--rock': css(mix(l2, mix(sk[2], hex('#b9a89a'), 0.5), 0.26)), '--rock-l': css(mix(l2, mix(sk[2], hex('#e6d8c8'), 0.5), 0.4)),
        '--sky-lamp': clamp(1 - t * 2.4, 0, 1).toFixed(2),
        // ближчі схили з деревами: темніші за далекі береги, низ — у тумані; скеля на них — у тому ж світлі
        '--l2b': css(l2bN), '--l2bm': css(mix(l2bN, mist, 0.08)),
        '--rock2': css(rock2N), '--rock2-l': css(mix(rock2N, mix(sk[2], hex('#e6d8c8'), 0.5), 0.4)),
        '--l3': css(l3N), '--l3m': css(mix(l3N, mist, 0.06)), '--l4': css(l4), '--l4b': css(mix(l4, BG, 0.5)),
        '--mist': css(mist), '--sky-mist': Math.min(1, c.mist * (k === 'fog' ? 1.8 : wet ? 1.25 : 1)).toFixed(2),
        // спільний колір сторінки: колір обрію цієї миті — ним підсвічено рамки й блоки по всьому сайту
        // звідки падає світло на блоки: сонце (вночі місяць) іде зліва направо — відблиск на блоках іде за ним
        '--sun-x': Math.round(50 + 42 * sinE) + '%',
        '--sh-x': Math.round(-18 * sinE) + 'px',
        // ближні сосни: не чорні силуети, а в тому ж серпанку, що й усе
        '--l3t': css(l3tN), '--l3tm': css(l3N),
        // тіні на воді: темні, а в хмарну погоду слабші (сонця нема — нема й тіні)
        // Тінь бере колір неба над нею: удень синьо-зелена, на заході синя, вночі майже чорна.
        '--shd': css(mix(mix(sk[0], g, 0.3), hex('#020806'), 0.6)), '--sky-shd': ((1 - cover) * 0.5 + 0.1).toFixed(2),
        '--shade': css(mix(mix(c.sk[0], grey[0], cover), hex('#020504'), 0.36)),
        // об'єм: тіньовий бік дерев, схилів і скель — з боку, протилежного сонцю; у хмарну погоду майже зникає
        '--sh-l': (clamp(0.5 + sinE * 2, 0, 1) * 0.3 * (1 - cover * 0.7)).toFixed(2),
        '--sh-r': (clamp(0.5 - sinE * 2, 0, 1) * 0.3 * (1 - cover * 0.7)).toFixed(2),
        // пора року: листя крон (взимку дерева голі), латаття й жовті глечики — наш знак, тож є завжди, крім зими (грудень — лютий)
        '--leaf': css(leafN(LEAF)), '--leaf2': css(leafN(LEAF2)), '--leaf3': css(leafN(LEAF3)), '--leaf-o': clamp(1 - 1.25 * wWin, 0, 1).toFixed(2),
        // сніг лежить серед зими, цвіт — у розпал весни, опале листя на воді — у розпал осені
        '--snow': css(mix(hex('#f4f8fb'), sk[2], 0.28)), '--snow-o': clamp((wWin - 0.5) * 4, 0, 1).toFixed(2),
        '--bloom-o': clamp((wSpr - 0.62) * 4, 0, 1).toFixed(2), '--fall-o': clamp((wAut - 0.55) * 3, 0, 1).toFixed(2),
        '--pine': css(mix(near(0.74, mix(sk[2], gE, 0.8), 0.2), mist, 0.07)),
        '--pad-o': clamp((0.62 - wWin) * 5, 0, 1).toFixed(2), '--fl-o': clamp((0.62 - wWin) * 5, 0, 1).toFixed(2),
        '--tint': css(mix(mix(c.sk[2], grey[2], cover), g, 0.12)),
        '--w0': css(mix(mix(sk[2], sk[1], 0.3), mix(sk[2], hex('#ffffff'), 0.3), ice * 0.6)),
        '--w1': css(mix(mix(sk[1], g, 0.62), mix(sk[1], hex('#dfe8ea'), 0.35), ice * 0.55)),
        '--pad': css(mix(g, hex('#3f9e68'), 0.25 + 0.25 * t)),
        // світила: --sky-orb — колір того, що зараз світить (ним мальовано сяйво в небі й доріжку на воді)
        '--sky-orb': css(orbC), '--sun-c': css(sunC), '--moon-c': css(moonC),
        '--sky-halo': (Math.max(c.halo * 1.3, 0.5) * (1 - cover) * litK).toFixed(3),
        // доріжка на воді: під серпом тьмяніша й вужча, під повнею — яскрава й широка; у затемнення лишається, але мідна й слабка
        '--sky-oo': (clearK * (day ? 1 : moonUp ? (0.3 + 0.7 * m.k) * (1 - 0.45 * m.ecl) : 0)).toFixed(2),
        '--refl-w': (day ? sizeOf(s, 3) : moonUp ? sizeOf(m, 2.6) * (0.4 + 0.6 * m.k) : 1).toFixed(2),
        '--sun-o': clearK.toFixed(2),
        '--moon-o': (clearK * (1 - 0.68 * clamp(t * 2, 0, 1))).toFixed(2),                 // удень місяць блідий
        '--orb-blur': (1.5 + 4 * cover + (k === 'fog' ? 3 : 0)).toFixed(1) + 'px',          // за серпанком сонце розпливається
        '--orb-k': sizeOf(s, 3).toFixed(2),
        '--birch': css(mix(hex('#e9ece4'), l3N, 0.5)),                                      // стовбури беріз: світлі, але не білі — ліс у нас темний
        '--sky-stars': (c.stars * (1 - clamp(cloud / 100, 0, 1) * 0.9) * (1 - 0.45 * (moonUp ? m.k : 0))).toFixed(2),   // при повні зір менше
        '--fly-o': clamp((wSum + wSpr * 0.5 - 0.3) * 2, 0, 1).toFixed(2),                  // світляки — теплої пори
        // старі назви: смужка під шапкою й відблиск угорі сторінки
        '--sky1': css(sk[1]), '--sky2': css(sk[2]),
      },
      text: wx && wx.t != null && !(fake && fake.wx) ? (wx.t > 0 ? '+' : wx.t < 0 ? '−' : '') + Math.abs(Math.round(wx.t)) + '°, ' + word(wx.code, k)
        : fake && fake.wx ? WORD[fake.wx] || '' : '',
      place: (wx && wx.name) || '',
    };
  }

  /// Де на сцені стоять сонце й місяць: пейзаж розтягується на всю ширину й обрізається, тож рахуємо від справжніх
  /// розмірів. Обидва йдуть дугою зліва направо за своїм справжнім часовим кутом; узимку дуга вужча й нижча, улітку —
  /// ширша й вища (так само, як у небі). Місяць сходить і заходить у свій час, тож буває видний і вдень, а вночі — ні.
  function place() {
    const el = document.getElementById('sky');
    if (!el || !el.clientWidth || !pos) return;
    const W = el.clientWidth, H = el.clientHeight;
    // На телефоні малюнок нижчий за сцену (над ним — саме небо), тож масштаб беремо з малюнка, а не зі сцени.
    const svg = el.querySelector('.sky-art svg');
    const s = Math.max(W / ART_W, ((svg && svg.clientHeight) || H) / ART_H);
    const vis = W / s;                                        // скільки одиниць пейзажу видно в ширину
    const left = (ART_W - vis) / 2;
    // Ліворуч у небі стоїть заголовок, праворуч на широкому екрані — балачки: світила ходять у проміжку між ними,
    // якраз над мостом (на телефоні — правою половиною), щоб не сідати ні на текст, ні за панель.
    const narrow = el.dataset.anchor === 'narrow';
    const xOf = (b) => { const u = clamp(b.ha / 2.1, -1, 1); return left + vis * (narrow ? 0.8 + 0.08 * u : 0.52 + 0.16 * u); };
    // Висота — частка від найвищої точки, але не чесна: інакше зимове сонце (16° опівдні) весь день лежало б на пагорбах.
    // Беремо середнє між «сьогоднішнім полуднем» і «літнім»: узимку опівдні світило стоїть на півдорозі, улітку — вгорі.
    // Нижче обрію воно тоне в далеких пагорбах. Угорі сцени лежить шапка сайту — під неї світила не заходять.
    const hdr = (document.querySelector('header') || {}).offsetHeight || 56;
    const horizon = HORIZON * s, room = Math.max(60, H - horizon - hdr - 48);
    const yOf = (b) => Math.min(H - hdr - 36, horizon + (b.alt >= 0 ? (b.alt / Math.max(12, Math.sqrt(b.top * pos.sumTop))) * room : b.alt * 3.3 * s));
    const put = (name, x, y) => { el.style.setProperty('--' + name + 'l', ((x - left) * s).toFixed(1) + 'px'); el.style.setProperty('--' + name + 'b', y.toFixed(1) + 'px'); };
    put('o', xOf(pos.sun), yOf(pos.sun));
    put('m', xOf(pos.moon), yOf(pos.moon));
    const L = pos.lit || pos.sun, xu = xOf(L);                // сяйво, доріжка й відблиск на воді — під тим, що світить
    put('h', xu, yOf(L));
    el.style.setProperty('--rx', xu.toFixed(1));
    // хмари: картинка 2400×300, тож один її оберт — у вісім висот шару
    const cl = el.querySelector('.sky-clouds');
    if (cl) el.style.setProperty('--cl-p', (cl.clientHeight * 8).toFixed(0) + 'px');
    // птахи: місце зграї — частка видимої ширини й висоти неба (від шапки до далеких пагорбів), самі птахи — в одиницях малюнка
    const bg = el.querySelector('.ld-birds');
    if (bg) {
      const top = Math.max(ART_Y + 6, ART_Y + ART_H - Math.min((svg && svg.clientHeight) || H, H - hdr - 10) / s), bot = 296;
      bg.innerHTML = flocks.map((f) => {
        // зграї тримаються відкритого неба: на широкому екрані — між заголовком і балачками (там, де ходять світила),
        // низькі — ще вужче, над річкою між берегами (з боків там уже ліс); на телефоні — на всю ширину
        const cx = left + vis * (f.w > 0.6 ? 0.38 + 0.24 * f.u : narrow ? 0.1 + 0.8 * f.u : 0.3 + 0.4 * f.u), cy = top + (bot - top) * f.w;
        let d = '';
        for (const [dx, dy, sz, fl, tilt] of f.list) {
          // птах — два крила від тулуба: кінці піднято «галочкою» (fl = 1), рівно (0) чи опущено (< 0); кожне крило вигнуте догори
          const c = Math.cos(tilt), sn = Math.sin(tilt), rot = (x, y) => (x * c - y * sn).toFixed(1) + ' ' + (x * sn + y * c).toFixed(1);
          const lx = -sz * f.dir, ly = -fl * sz * 0.45, rx = sz * 0.9 * f.dir, ry = -fl * sz * 0.4, up = sz * 0.3;
          d += 'M' + (cx + dx + lx * c - ly * sn).toFixed(1) + ' ' + (cy + dy + lx * sn + ly * c).toFixed(1) +
            'q' + rot(-lx / 2, -ly / 2 - up) + ' ' + rot(-lx, -ly) + 'q' + rot(rx / 2, ry / 2 - up) + ' ' + rot(rx, ry);
        }
        return '<path style="stroke-width:' + f.sw + ';opacity:' + f.op + '" d="' + d + '"/>';
      }).join('');
    }
    // Тіні на воді падають у бік від світила; що нижче воно стоїть, то вони довші.
    const lean = (pos.lit ? -34 * Math.sin(pos.lit.ha) : 0).toFixed(1);
    const long = (0.5 + 0.45 * (1 - clamp(pos.lit ? pos.lit.alt / Math.max(12, pos.lit.top) : 1, 0, 1))).toFixed(2);
    el.querySelectorAll('[data-shd]').forEach((sh) => {
      const y = sh.dataset.shd;
      sh.setAttribute('transform', 'translate(0 ' + y + ') skewX(' + lean + ') scale(1 -' + long + ') translate(0 -' + y + ')');
    });
  }

  // ---------------------------------------------------------------------------------------------
  // небо, що генерується: хмари, дощ і сніг — під погоду й випадково (щоразу, як відкрили сторінку, інакші)
  // ---------------------------------------------------------------------------------------------

  let seed = (Math.random() * 1e9) | 0;          // ?seed=7 в адресі — те саме небо щоразу (для перевірок)
  const rng = (s) => () => {
    s = (s + 0x6d2b79f5) | 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };

  /// Хмари — окремий шар за пейзажем, намальований на полотні. Кожну хмару ліпимо з десятків і сотень м'яких кульок,
  /// тож окремих фігур у ній не видно: спершу тіло, потім світло на боці від сонця, потім тінь зісподу. Що буде в небі,
  /// вирішує погода (ясно, мінлива хмарність, хмарно, пелена, дощ, гроза, туман), а яке саме небо з кількох можливих
  /// для цієї погоди, скільки хмар, де й якого розміру — випадок. Види: пір'ясті пасма (часом із гачком), баранці
  /// рядами, малі купчасті, купчасті вежі, пелена з прогалинами, вежа з ковадлом. Картинка повторюється по ширині й
  /// повільно пливе (CSS), шва нема: кульки біля краю домальовано з іншого боку.
  let cloudKey = '', cloudUrl = '';
  function clouds(v) {
    const el = document.querySelector('#sky .sky-clouds');
    if (!el) return;
    const band = v.kind === 'fog' ? 'fog' : v.kind === 'storm' ? 'storm' : v.kind === 'rain' || v.kind === 'snow' ? 'wet'
      : v.cloud < 12 ? 'clear' : v.cloud < 45 ? 'few' : v.cloud < 80 ? 'many' : 'over';
    const key = band + '|' + v.cl.join(',') + '|' + v.cl2.join(',');
    if (key === cloudKey) return;
    cloudKey = key;
    const W = 2400, H = 300, cv = document.createElement('canvas');
    cv.width = W; cv.height = H;
    const g = cv.getContext('2d');
    const r = rng(seed), R = (a, b) => a + (b - a) * r(), N = (a, b) => Math.floor(R(a, b));
    const dark = band === 'wet' || band === 'storm' || band === 'over';
    const cMid = dark ? mix(v.cl2, v.cl, 0.22) : mix(v.cl, v.cl2, 0.3);
    const cLit = dark ? mix(v.cl2, v.cl, 0.5) : mix(v.cl, [255, 255, 255], 0.14);     // світло й тінь тихі — хмара не має перетягувати увагу
    const cDark = dark ? mix(v.cl2, [0, 0, 0], 0.16) : mix(v.cl2, v.cl, 0.4);
    const sprite = (c) => {                        // м'яка кулька — «глина», з якої ліпимо
      const s = document.createElement('canvas');
      s.width = s.height = 64;
      const q = s.getContext('2d'), gr = q.createRadialGradient(32, 32, 0, 32, 32, 32), rgb = c.join(',');
      gr.addColorStop(0, 'rgba(' + rgb + ',1)'); gr.addColorStop(0.5, 'rgba(' + rgb + ',.8)'); gr.addColorStop(1, 'rgba(' + rgb + ',0)');
      q.fillStyle = gr; q.fillRect(0, 0, 64, 64);
      return s;
    };
    const sMid = sprite(cMid), sLit = sprite(cLit), sDark = sprite(cDark);
    const dab = (s, x, y, rad, a, flat) => {           // flat < 1 — кулька сплюснута (для шарів і баранців)
      const ry = rad * (flat || 1);
      g.globalAlpha = Math.min(1, a);
      for (const dx of [0, W, -W]) if (x + dx + rad > 0 && x + dx - rad < W) g.drawImage(s, x + dx - rad, y - ry, rad * 2, ry * 2);
    };
    const side = pos && pos.lit && Math.sin(pos.lit.ha) < 0 ? -1 : 1;              // з якого боку світло
    const shape = (bl, litA, darkA) => {
      g.globalCompositeOperation = 'source-over';
      for (const b of bl) dab(sMid, b[0], b[1], b[2], b[3], b[4]);
      g.globalCompositeOperation = 'source-atop';
      for (const b of bl) dab(sLit, b[0] + side * b[2] * 0.25, b[1] - b[2] * 0.4 * (b[4] || 1), b[2] * 0.75, b[3] * litA, b[4]);
      for (const b of bl) dab(sDark, b[0], b[1] + b[2] * 0.55 * (b[4] || 1), b[2] * 0.8, b[3] * darkA, b[4]);
      g.globalCompositeOperation = 'source-over';
    };
    const cumulus = (x, y, w, h) => {              // пласка основа, згори — кілька випадкових горбів
      const n = Math.round(((w * h) / 420) * R(0.8, 1.3)) + 14, bl = [], hp = [];
      for (let i = N(2, 6); i--;) hp.push([R(-0.8, 0.8), R(0.45, 1), R(0.18, 0.5)]);
      for (let i = 0; i < n; i++) {
        const u = R(-1, 1);
        let top = 0.25 * (1 - u * u);
        for (const q of hp) top = Math.max(top, q[1] * Math.exp(-((u - q[0]) ** 2) / (2 * q[2] * q[2])));
        bl.push([x + (u * w) / 2, y - h * top * Math.pow(r(), 0.7), (h * 0.16 + h * 0.3 * r()) * (0.55 + 0.45 * top), R(0.5, 0.9)]);
      }
      shape(bl, 0.75, 0.55);
    };
    const cirrus = (x, y, len, a) => {             // тонке пасмо вздовж ледь кривої лінії
      const ang = R(-0.12, 0.12), hook = r() < 0.5 ? R(20, 60) : 0, n = Math.round(len / 5);
      let wob = 0;
      g.globalCompositeOperation = 'source-over';
      for (let i = 0; i < n; i++) {
        const f = i / n;
        wob = (wob + R(-1.2, 1.2)) * 0.93;
        dab(sLit, x + f * len, y + Math.sin(ang) * f * len - hook * Math.pow(1 - f, 3) + wob, R(3, 7) * (0.6 + Math.sin(f * Math.PI)), a * R(0.5, 1) * Math.sin(Math.max(0.05, f) * Math.PI));
      }
    };
    const flock = (x, y, w, h) => {                // баранці: дрібні купки рядами, до країв поля рідшають
      const bl = [], dx = R(26, 40), dy = R(15, 22), sl = R(-0.25, 0.25);
      for (let yy = -h / 2; yy < h / 2; yy += dy) for (let xx = -w / 2; xx < w / 2; xx += dx) {
        const e = 1 - Math.max(Math.abs(xx) / (w / 2), Math.abs(yy) / (h / 2));
        if (r() > e * 1.5) continue;
        // кожен баранець — дві-три сплюснуті кульки, що зливаються із сусідами, а не окрема крапка
        for (let q = N(2, 4); q--;) bl.push([x + xx + R(-12, 12) + yy * sl * 2, y + yy + R(-4, 4), R(13, 24), R(0.22, 0.5) * Math.min(1, e * 2.2), R(0.4, 0.62)]);
      }
      shape(bl, 0.6, 0.35);
    };
    const deck = (y0, y1, n, a) => { const bl = []; for (let i = 0; i < n; i++) bl.push([R(0, W), R(y0, y1), R(70, 170), a * R(0.5, 1), R(0.45, 0.8)]); shape(bl, 0.35, 0.6); };
    const anvil = (x, y) => {                      // грозова вежа з пласким ковадлом угорі
      cumulus(x, y, R(260, 360), R(170, 210));
      const bl = [];
      for (let i = 0; i < 90; i++) { const u = R(-1, 1); bl.push([x + u * R(300, 420), y - R(175, 215) + Math.abs(u) * 14, R(16, 34), R(0.5, 0.85)]); }
      shape(bl, 0.7, 0.45);
    };
    const many = (n, f) => { for (let i = n; i--;) f(); };
    const skies = {
      clear: [
        () => many(N(1, 5), () => cirrus(R(0, W), R(20, 150), R(260, 760), 0.16)),
        () => { if (r() < 0.6) flock(R(0, W), R(40, 110), R(300, 620), R(70, 120)); many(N(0, 3), () => cirrus(R(0, W), R(20, 170), R(200, 560), 0.14)); },
        () => { cumulus(R(0, W), R(150, 220), R(110, 190), R(34, 54)); cirrus(R(0, W), R(20, 120), R(300, 700), 0.14); },
        () => {},
      ],
      few: [
        () => many(N(3, 7), () => cumulus(R(0, W), R(110, 230), R(130, 300), R(40, 86))),
        () => { many(N(1, 3), () => cumulus(R(0, W), R(170, 235), R(240, 420), R(110, 170))); many(N(2, 5), () => cumulus(R(0, W), R(120, 220), R(90, 170), R(28, 46))); },
        () => { many(N(1, 3), () => flock(R(0, W), R(50, 130), R(380, 760), R(90, 150))); many(N(1, 4), () => cumulus(R(0, W), R(150, 230), R(120, 240), R(36, 70))); },
        () => { many(N(2, 5), () => cirrus(R(0, W), R(20, 140), R(300, 800), 0.18)); many(N(2, 4), () => cumulus(R(0, W), R(140, 230), R(140, 280), R(44, 90))); },
      ],
      many: [
        () => many(N(8, 14), () => cumulus(R(0, W), R(90, 235), R(160, 380), R(50, 120))),
        () => { deck(40, 120, 26, 0.3); many(N(5, 9), () => cumulus(R(0, W), R(130, 235), R(200, 420), R(60, 130))); },
        () => { many(N(3, 5), () => flock(R(0, W), R(50, 150), R(500, 900), R(110, 170))); many(N(3, 6), () => cumulus(R(0, W), R(150, 235), R(180, 340), R(60, 110))); },
      ],
      over: [
        () => { deck(10, 150, 70, 0.5); deck(120, 230, 40, 0.42); },
        () => { deck(10, 120, 60, 0.5); many(N(5, 9), () => cumulus(R(0, W), R(150, 240), R(260, 520), R(60, 110))); },
      ],
      wet: [
        () => { deck(0, 150, 90, 0.6); deck(120, 240, 50, 0.5); many(N(4, 9), () => cumulus(R(0, W), R(200, 262), R(120, 260), R(26, 44))); },
        () => { deck(0, 130, 80, 0.6); many(N(4, 7), () => cumulus(R(0, W), R(160, 250), R(300, 560), R(80, 140))); },
      ],
      storm: [() => { deck(0, 140, 80, 0.62); anvil(R(300, W - 300), 250); if (r() < 0.5) anvil(R(300, W - 300), 255); many(N(3, 6), () => cumulus(R(0, W), R(200, 260), R(140, 300), R(30, 50))); }],
      fog: [() => deck(150, 290, 46, 0.4), () => { deck(120, 290, 60, 0.36); many(N(0, 3), () => cirrus(R(0, W), R(20, 90), R(300, 700), 0.12)); }],
    };
    const sky = skies[band];
    sky[Math.floor(r() * sky.length)]();
    el.dataset.band = band;
    cv.toBlob((b) => {
      if (!b) return;
      const u = URL.createObjectURL(b);
      el.style.backgroundImage = 'url(' + u + ')';
      if (cloudUrl) URL.revokeObjectURL(cloudUrl);
      cloudUrl = u;
    });
    // плями кольору в самому небі теж щоразу лягають трохи інакше
    const sc = document.getElementById('sky'), p = (a, b) => R(a, b).toFixed(0) + '%';
    sc.style.setProperty('--b1x', p(6, 30)); sc.style.setProperty('--b1y', p(18, 42));
    sc.style.setProperty('--b2x', p(70, 94)); sc.style.setProperty('--b2y', p(6, 26)); sc.style.setProperty('--b3x', p(36, 64));
  }

  /// Місяць. Обличчя в нього завжди те саме, тож «моря» лежать там, де на справжньому: Океан Бур і Море Дощів — ліворуч
  /// угорі, Ясності, Спокою, Криз і Достатку — праворуч, Хмар і Вологості — ліворуч унизу, світлий кратер Тихо — внизу.
  /// Це м'які плями з нерівними краями (зсув шумом і розмиття), без жодної чіткої фігури. Фаза — не вирізаний шматок:
  /// край диска чіткий, а межа світла й тіні розмита, світло до неї згасає; темний бік ледь видно лише при вузькому
  /// серпі (попелясте світло). Серп нахиляється, поки місяць іде небом.
  const MARIA = 'M34 14Q44 12 51 18Q55 25 51 32Q47 37 40 37Q37 40 38 46Q40 52 36 57Q33 61 27 60Q20 60 15 53Q9 45 9.5 37Q10 28 16 22Q22 15 34 14Z' +
    'M55 26Q56 20 63 20Q70 21 70.5 28Q70 35 63 36.5Q56 36 55 26Z' + 'M66 40Q68 34 75 35Q83 37 83 44Q82 51 76 53Q68 53 66 47Q65 43 66 40Z' +
    'M85 31Q88.5 29 90.5 33Q92.5 38 90 42Q87 44 85 40Q83.5 35 85 31Z' + 'M81 50Q85 47 89 51Q92 57 89 63Q85 66 82 62Q79 56 81 50Z' +
    'M72.5 60Q74 57.5 77.5 58.5Q80 61 78.5 64.5Q75.5 66.5 73 64.5Q71.5 62 72.5 60Z' + 'M29 65Q31 58 39 58.5Q47 60 47.5 67Q46 75 38 76Q30 75 29 65Z' +
    'M20.5 67Q22 63.5 26 64.5Q28.5 67.5 27.5 71.5Q25 74.5 22 73Q19.5 70.5 20.5 67Z' + 'M31 13.5Q48 8.5 66 12Q68 14 64 14.5Q48 12 33 16Q30 15.5 31 13.5Z' +
    'M50 38Q52.5 36 55.5 38Q56.5 41 54 42.5Q51 43 50 38Z';
  let moonKey = '';
  function moonImg(v) {
    const el = document.querySelector('#sky .sky-moon');
    if (!el) return;
    el.style.transform = 'rotate(' + (Math.sin(v.moon.ha) * 30).toFixed(0) + 'deg) scale(' + v.moon.s.toFixed(3) + ')';
    const k = Math.round(v.moon.k * 40) / 40, col = (c) => 'rgb(' + c.join(',') + ')', key = [k, v.moon.wax, v.moonRgb, v.moon.e].join('|');
    if (key === moonKey) return;
    moonKey = key;
    // освітлений бік — півплощина від термінатора й далеко за диск: край диска ріже clipPath (він лишається чітким),
    // а розмиття дістається лише термінатору; при вузькому серпі розмиваємо менше, щоб він не зник
    const lit = 'M50 -40H150V140H50V96A' + (Math.abs(1 - 2 * k) * 46).toFixed(1) + ' 46 0 0 ' + (k < 0.5 ? 0 : 1) + ' 50 4Z';
    const svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><defs><clipPath id="d"><circle cx="50" cy="50" r="46"/></clipPath>' +
      '<filter id="t" filterUnits="userSpaceOnUse" x="-60" y="-60" width="220" height="220"><feGaussianBlur stdDeviation="' + (1.2 + 7 * Math.min(k, 1 - k)).toFixed(1) + '"/></filter>' +
      '<filter id="s" x="-20%" y="-20%" width="140%" height="140%"><feTurbulence type="fractalNoise" baseFrequency=".07" numOctaves="2" seed="11" result="n"/>' +
      '<feDisplacementMap in="SourceGraphic" in2="n" scale="7" xChannelSelector="R" yChannelSelector="G"/><feGaussianBlur stdDeviation="1.5"/></filter>' +
      '<filter id="r" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="1.1"/></filter>' +
      '<filter id="n"><feTurbulence type="fractalNoise" baseFrequency=".11" numOctaves="3" seed="5"/><feColorMatrix values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1.5 0 0 0 -.5"/></filter>' +
      '<mask id="m" maskUnits="userSpaceOnUse" x="0" y="0" width="100" height="100"><g filter="url(#t)"><path fill="#fff" d="' + lit + '"' + (v.moon.wax ? '' : ' transform="translate(100 0) scale(-1 1)"') + '/></g></mask>' +
      '<radialGradient id="g"><stop offset="0" stop-color="' + col(mix(v.moonRgb, [255, 255, 255], 0.22)) + '"/><stop offset=".72" stop-color="' + col(v.moonRgb) + '"/>' +
      '<stop offset="1" stop-color="' + col(mix(v.moonRgb, [38, 44, 58], 0.16)) + '"/></radialGradient></defs>' +
      '<g clip-path="url(#d)"><circle cx="50" cy="50" r="46" fill="' + col(v.moonRgb) + '" opacity="' + v.moon.e + '"/>' +
      '<g' + (k > 0.97 ? '' : ' mask="url(#m)"') + '><circle cx="50" cy="50" r="46" fill="url(#g)"/>' +
      '<path d="' + MARIA + '" fill="' + col(mix(v.moonRgb, [38, 44, 58], 0.62)) + '" opacity=".36" filter="url(#s)"/>' +
      '<rect width="100" height="100" filter="url(#n)" opacity=".16"/>' +
      '<g fill="' + col(mix(v.moonRgb, [255, 255, 255], 0.5)) + '" filter="url(#r)"><circle cx="43.6" cy="81.4" r="2.6" opacity=".55"/><circle cx="43.6" cy="81.4" r="8" opacity=".12"/>' +
      '<circle cx="34.5" cy="42.3" r="1.8" opacity=".5"/><circle cx="22" cy="43.6" r="1.2" opacity=".45"/><circle cx="18.9" cy="31.5" r="1.3" opacity=".55"/></g></g></g></svg>';
    el.style.setProperty('--moon-img', 'url("data:image/svg+xml,' + encodeURIComponent(svg) + '")');
    el.style.setProperty('--moon-k', (0.3 + 0.7 * k).toFixed(2));
  }

  /// Птахи. Хто літає — вирішують пора року, година й погода, а скільки їх, де й куди летять — випадок (за десять хвилин
  /// у небі вже інші). Восени й навесні — ключі перелітних; з осені до весни, надто вдосвіта й надвечір, — зграї граків
  /// і галок; теплої пори — ластівки (у хмарну погоду й перед дощем літають низько) та лелека чи яструб, що кружляє
  /// високо; взимку — кілька ворон. У дощ, сніг, грозу й туман птахів нема, уночі теж; буває й просто порожнє небо.
  /// Тут лише розкладаємо зграї, малює їх place() — у порожню групу ld-birds у пейзажі.
  let flocks = [], birdsKey = '';
  function birds(v) {
    const key = [seed, v.birds.slot, v.kind, v.phase, v.code, Math.round(v.cloud / 25)].join('|');
    if (key === birdsKey) return;
    birdsKey = key;
    flocks = [];
    const r = rng(seed * 31 + v.birds.slot * 7919 + 5), R = (a, b) => a + (b - a) * r();
    const drizzle = v.kind === 'rain' && v.code >= 51 && v.code <= 57;
    if (v.phase === 'night' || v.kind === 'fog' || v.kind === 'storm' || v.kind === 'snow' || (v.kind === 'rain' && !drizzle)) return;
    const twilight = v.phase !== 'day', grey = v.cloud > 70;
    if (r() < (drizzle ? 0.7 : grey ? 0.4 : 0.18)) return;
    const [wWin, wSpr, wSum, wAut] = v.birds.w;
    const kinds = [
      ['skein', 1.5 * wAut + 0.9 * wSpr],
      ['rooks', (wAut + wWin + 0.3 * wSpr) * (twilight ? 2.4 : 0.5)],
      ['swallows', (1.5 * wSum + 0.8 * wSpr + 0.2 * wAut) * (twilight ? 1.3 : 1)],
      ['soar', (0.9 * wSum + 0.6 * wSpr + 0.25 * wAut) * (twilight || grey ? 0.15 : 1)],
      ['crows', 1.3 * wWin + 0.35 * wAut + 0.1],
    ];
    const total = kinds.reduce((s, x) => s + x[1], 0);
    const pick = () => { let x = r() * total; for (const q of kinds) { x -= q[1]; if (x <= 0) return q[0]; } return kinds[0][0]; };
    const n = r() < 0.5 ? 1 : r() < 0.7 ? 2 : 3, seen = {};
    for (let i = 0; i < n; i++) {
      const kind = pick();
      if (seen[kind]) continue;
      seen[kind] = 1;
      const dir = r() < 0.5 ? -1 : 1, list = [];
      const bird = (dx, dy, s, f) => list.push([dx, dy, s, f == null ? R(-0.35, 1) : f, R(-0.25, 0.25)]);
      let w, sw = 0.9, op = 0.9;
      if (kind === 'skein') {
        // ключ: вожак попереду, за ним клином два крила різної довжини, птахи рівно один за одним
        const cnt = Math.round(R(7, 19)), s = R(2.8, 4), gap = s * R(1.7, 2.2), ang = R(0.42, 0.62), one = Math.round(cnt * R(0.3, 0.7));
        bird(0, 0, s);
        for (let a = 0; a < 2; a++) for (let q = 1; q <= (a ? cnt - one : one); q++)
          bird(-dir * q * gap * Math.cos(ang) + R(-0.4, 0.4), (a ? 1 : -1) * q * gap * Math.sin(ang) * 0.8 + q * gap * 0.12 + R(-0.4, 0.4), s * R(0.9, 1.1));
        w = R(0.05, 0.42); sw = 0.8; op = 0.8;
      } else if (kind === 'rooks') {
        const cnt = Math.round(R(12, 34)), sx = R(60, 130), sy = R(14, 30);
        for (let q = 0; q < cnt; q++) bird((r() + r() + r() - 1.5) * sx, (r() + r() + r() - 1.5) * sy, R(2, 4));
        w = R(0.15, 0.58); sw = 0.85; op = 0.85;
      } else if (kind === 'swallows') {
        const cnt = Math.round(R(4, 11));
        for (let q = 0; q < cnt; q++) bird(R(-140, 140), R(-24, 24), R(2.4, 4));
        w = grey || drizzle ? R(0.74, 0.95) : R(0.3, 0.58);
      } else if (kind === 'soar') {
        bird(0, 0, R(7, 10), R(0.05, 0.3));
        if (r() < 0.35) bird(R(22, 46) * dir, R(-14, 14), R(5.5, 7.5), R(0.05, 0.3));
        w = R(0.04, 0.3); sw = 1.3;
      } else {
        const cnt = Math.round(R(2, 5));
        for (let q = 0; q < cnt; q++) bird(R(-90, 90), R(-22, 22), R(3.2, 5));
        w = R(0.2, 0.58);
      }
      flocks.push({ u: r(), w, dir, list, sw, op });
    }
  }

  /// Дощ і сніг: три шари глибини, у кожному краплі розкидано випадково — довжина, яскравість, місце. Шар — плитка, що
  /// повторюється, але нахил підібрано так, що за один оберт вона зсувається рівно на себе: шва в русі нема.
  /// Мряка — рідко й коротко, злива й гроза — густо й довго.
  let fallKey = '';
  function fall(kind, code) {
    const el = document.querySelector('#sky .sky-fall');
    if (!el) return;
    const heavy = code === 65 || code === 82 || kind === 'storm', light = code >= 51 && code <= 57, snow = kind === 'snow';
    const key = kind + (heavy ? 'h' : light ? 'l' : '');
    if (key === fallKey) return;
    fallKey = key;
    if (kind !== 'rain' && kind !== 'storm' && !snow) { el.style.backgroundImage = ''; return; }
    const r = rng(seed + 7), R = (a, b) => a + (b - a) * r(), W = 320, H = 400;
    const img = [], size = [], to = [];
    [[0.55, 0.55], [0.8, 0.8], [1.15, 1]].forEach(([scale, alpha], i) => {
      const n = Math.round((snow ? 34 : heavy ? 72 : light ? 20 : 44) * (i === 2 ? 0.6 : 1));
      const k = snow ? 1 : Math.round(R(3, 6)), tan = snow ? 0 : W / (k * H);
      let s = '';
      for (let q = 0; q < n; q++) {
        const x = R(0, W), y = R(0, H), a = (R(0.12, 0.5) * alpha).toFixed(2), len = R(10, heavy ? 54 : light ? 22 : 38), rad = R(0.8, 2.4), sw = R(0.7, 1.5).toFixed(1);
        for (const dx of snow ? [0, W, -W] : [0, W]) for (const dy of snow ? [0, H, -H] : [0, -H]) {
          s += snow ? '<circle cx="' + (x + dx).toFixed(1) + '" cy="' + (y + dy).toFixed(1) + '" r="' + rad.toFixed(1) + '" fill="rgba(255,255,255,' + a + ')"/>'
            : '<path d="M' + (x + dx).toFixed(1) + ' ' + (y + dy).toFixed(1) + 'l' + (-len * tan).toFixed(1) + ' ' + len.toFixed(1) + '" stroke="rgba(255,255,255,' + a + ')" stroke-width="' + sw + '" stroke-linecap="round"/>';
        }
      }
      img.push('url("data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="' + W + '" height="' + H + '" viewBox="0 0 ' + W + ' ' + H + '">' + s + '</svg>') + '")');
      size.push((W * scale).toFixed(0) + 'px ' + (H * scale).toFixed(0) + 'px');
      to.push((snow ? (i % 2 ? 1 : -1) * W * scale : -W * scale).toFixed(0) + 'px ' + (k * H * scale).toFixed(0) + 'px');
    });
    el.style.backgroundImage = img.join(', ');
    el.style.backgroundSize = size.join(', ');
    el.style.setProperty('--fall-to', to.join(', '));
    el.style.animationDuration = (snow ? 16 : heavy ? 1.7 : light ? 3.4 : 2.4) + 's';
  }

  let last = '';
  function paint() {
    const v = compute();
    const sig = JSON.stringify(v);
    if (sig !== last) {
      last = sig;
      const r = document.documentElement;
      for (const k of Object.keys(v.vars)) r.style.setProperty(k, v.vars[k]);
      r.dataset.sky = v.phase;
      r.dataset.wx = v.kind;
      document.querySelectorAll('[data-sky-text]').forEach((el) => {
        el.textContent = v.text ? (v.place ? v.place + ': ' : '') + v.text + '.' : '';
        el.hidden = !v.text;
      });
    }
    clouds(v);
    moonImg(v);
    birds(v);
    fall(v.kind, v.code);
    place();
  }

  /// Погода мого району (той, що вибрано для «Тривоги»): сервер тримає її чверть години, ми питаємо не частіше.
  function load(force) {
    if (!o || !o.api || (!force && Date.now() - wxAt < 10 * 60000)) return;
    wxAt = Date.now();
    const ob = o.oblast ? o.oblast() : '';
    o.api('GET', '/api/weather' + (ob ? '?oblast=' + encodeURIComponent(ob) : ''))
      .then((d) => { wx = d && !d.off ? d : null; paint(); })
      .catch(() => { /* сервер без погоди чи Open-Meteo мовчить — небо лишається ясним */ });
  }

  const parseTime = (s, day) => {
    const t = /^(\d{1,2}):(\d{2})$/.exec(s || ''), d = /^(?:(\d{4})-)?(\d{1,2})-(\d{1,2})$/.exec(day || '');
    if (!t && !d) return null;
    const at = new Date();
    if (d) { if (d[1]) at.setFullYear(+d[1]); at.setMonth(+d[2] - 1, +d[3]); }
    if (t) at.setHours(+t[1], +t[2], 0, 0);
    return at;
  };

  /// Пейзаж — окремий файл: вставляємо його в сцену розміткою (щоб кольори брались зі змінних), раз на сторінку.
  function mount() {
    const el = document.getElementById('sky');
    if (!el || el.dataset.ready) return;
    el.dataset.ready = '1';
    const narrow = window.matchMedia('(max-width: 900px)');
    const anchor = () => {
      // На телефоні видно лише середину пейзажу — міст; очерет з країв CSS підсуває до неї (.ld-reed-a / -b).
      el.dataset.anchor = narrow.matches ? 'narrow' : 'mid';
      place();
    };
    const art = (url, into) => fetch(url).then((r) => (r.ok ? r.text() : '')).then((t) => {
      if (!t) return;
      into.innerHTML = t;
      anchor();
    }).catch(() => { /* без малюнка лишається небо-градієнт */ });
    art('/static/obrii.svg', el.querySelector('.sky-art'));
    narrow.addEventListener?.('change', anchor);
    if (window.ResizeObserver) new ResizeObserver(place).observe(el);
    anchor();
    // Шапка на головній прозора — лежить просто в небі; щойно сторінку прокрутили, вертає собі тло.
    const scrolled = () => document.body.classList.toggle('scrolled', window.scrollY > 16);
    window.addEventListener('scroll', scrolled, { passive: true });
    scrolled();
  }

  window.HSky = {
    init(opts) {
      o = opts || {};
      const q = new URLSearchParams(location.search);
      if (q.get('seed')) seed = +q.get('seed') | 0;

      if (q.get('sky') || q.get('wx') || q.get('day') || q.get('cover')) fake = { at: parseTime(q.get('sky'), q.get('day')), cover: q.get('cover') != null ? +q.get('cover') : null, wx: WORD[q.get('wx')] ? q.get('wx') : null };
      mount();
      paint();
      setInterval(() => { if (!document.hidden) { paint(); load(false); } }, 5 * 60000);
      document.addEventListener('visibilitychange', () => { if (!document.hidden) { paint(); load(false); } });
    },
    /// Після /api/me і коли людина змінила район у «Тривозі».
    ready() { load(true); },
    /// Для перевірок і показу: HSky.preview({ time: '21:40', wx: 'snow' }); без аргументів — назад до справжнього.
    preview(p) {
      fake = p ? { at: parseTime(p.time, p.day), wx: p.wx && WORD[p.wx] ? p.wx : null } : null;
      paint();
    },
    /// Сцена з'явилась чи змінила розмір (зайшли на головну) — розставити світило й підписи ще раз.
    repaint() { last = ''; paint(); },
  };
})();
