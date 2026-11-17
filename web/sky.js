// 🌅 Небо (web/sky.js): сайт живе за справжнім часом — схід, день, захід, ніч — і за справжньою погодою.
// Де сонце, рахуємо самі (дата й координати, жодних сторонніх даних); хмари, дощ і сніг дає сервер (/api/weather:
// він сам питає Open-Meteo раз на чверть години, браузери людей туди не ходять). Нема погоди — небо просто ясне.
//
// Малює все CSS (static/polyva.css) і пейзаж static/obrii.svg — Тетерів у Житомирі: небо в три кольори, далекі
// пагорби, підвісний міст, береги каньйону, вода з відблиском; усе далеке тоне в тумані, чіткий лише очерет спереду.
// Колір обрію (--tint) і бік, звідки світить сонце (--sun-x, --sh-x), беруть фон і блоки всього сайту. Звідси йдуть лише змінні на <html> — кольори
// (--sk0…--sk2, --l1…--l4 і їхні «…m» у тумані, --lb міст, --rock, --w0, --w1, --pad, --mist), скільки туману
// (--sky-mist), ліхтарі на мосту (--sky-lamp), світило (--sky-orb, --sky-halo, --sky-oo), зорі й хмари
// (--sky-stars, --sky-cirrus, --sky-deck) — і два атрибути:
// data-sky (night | dawn | day | dusk) та data-wx (clear | cloud | fog | rain | snow | storm). Перераховуємо раз на
// п'ять хвилин і коли вкладка вертається; кольори не анімуються зовсім.
(function () {
  'use strict';
  let o = null;
  let wx = null;                 // остання відповідь /api/weather або null
  let wxAt = 0;
  let fake = null;               // перегляд: { at: Date | null, wx: 'rain' | … } — з адреси ?sky=06:10&wx=rain&day=01-15 (місяць-день) або HSky.preview()
  let pos = { alt: 0, ha: 0, day: true };
  const KYIV = { lat: 50.45, lon: 30.52 };
  const RAD = Math.PI / 180;
  // Пейзаж намальовано в цих одиницях (viewBox 0 90 2000 470); гребінь далеких пагорбів — на 236 одиниць вище за низ.
  const ART_W = 2000, ART_H = 470, HORIZON = 236;

  // ---------------------------------------------------------------------------------------------
  // де сонце
  // ---------------------------------------------------------------------------------------------

  /// Висота сонця над обрієм (градуси) й часовий кут (радіани, від'ємний — до полудня). Спрощена формула з
  /// астрономічного альманаху: похибка до градуса, для кольору неба — із запасом.
  function sun(date, lat, lon) {
    const d = (date.getTime() - Date.UTC(2000, 0, 1, 12)) / 864e5;
    const L = (280.46 + 0.9856474 * d) % 360;
    const g = ((357.528 + 0.9856003 * d) % 360) * RAD;
    const lam = (L + 1.915 * Math.sin(g) + 0.02 * Math.sin(2 * g)) * RAD;
    const eps = (23.439 - 0.0000004 * d) * RAD;
    const ra = Math.atan2(Math.cos(eps) * Math.sin(lam), Math.cos(lam));
    const dec = Math.asin(Math.sin(eps) * Math.sin(lam));
    const gmst = (18.697374558 + 24.06570982441908 * d) % 24;
    let ha = (gmst * 15 + lon) * RAD - ra;
    ha = Math.atan2(Math.sin(ha), Math.cos(ha));
    const alt = Math.asin(Math.sin(lat * RAD) * Math.sin(dec) + Math.cos(lat * RAD) * Math.cos(dec) * Math.cos(ha));
    // top — найвища точка, куди сонце сьогодні підніметься (у полудень): від неї рахуємо, наскільки високо воно на сцені
    return { alt: alt / RAD, ha, top: 90 - Math.abs(lat - dec / RAD) };
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
  const LEAF = ['#7f8d88', '#6f9f52', '#3a7355', '#a8782f'].map(hex);
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
    const s = sun(at, p.lat, p.lon);
    const morning = s.ha < 0;
    // −10° і нижче — ніч; до +6° — сутінки (уранці персикові, увечері жовтогарячі з бузком); вище — день.
    const t = clamp((s.alt + 10) / 16, 0, 1);
    const tw = morning ? P.dawn : P.dusk;
    const c = t < 0.5 ? blend(P.night, tw, t * 2) : blend(tw, P.day, (t - 0.5) * 2);
    const phase = t <= 0.12 ? 'night' : t >= 0.92 ? 'day' : morning ? 'dawn' : 'dusk';
    // Погода: хмарність тягне небо й землю до сірого, дощ і сніг — ще трохи.
    const k = fake && fake.wx ? fake.wx : wx ? kind(wx.code) : 'clear';
    const wet = k === 'rain' || k === 'snow' || k === 'storm' || k === 'fog';
    const cloud = fake && fake.wx ? (fake.wx === 'clear' ? 0 : 90) : wx && wx.cover != null ? wx.cover : k === 'clear' ? 0 : 70;
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
    const l2bN = near(0.74, l2b, 0.08), l3N = near(0.88, l3, 0.1), l3tN = near(0.86, mix(l3e, sk[2], 0.3), 0.1);
    const rock2N = near(0.62, mix(l2b, mix(sk[2], hex('#b9a89a'), 0.5), 0.34), 0.08);

    pos = { alt: s.alt, ha: s.ha, top: s.top, day: s.alt > -4 };
    const sinE = Math.sin(pos.day ? s.ha : s.ha - Math.PI);   // де світило: −1 — ліворуч, +1 — праворуч
    return {
      phase, kind: k,
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
        '--sun-x': Math.round(50 + 42 * Math.sin(s.alt > -4 ? s.ha : s.ha - Math.PI)) + '%',
        '--sh-x': Math.round(-18 * Math.sin(s.alt > -4 ? s.ha : s.ha - Math.PI)) + 'px',
        // ближні сосни: не чорні силуети, а в тому ж серпанку, що й усе
        '--l3t': css(l3tN), '--l3tm': css(mix(l3tN, mist, 0.06)),
        // тіні на воді: темні, а в хмарну погоду слабші (сонця нема — нема й тіні)
        // Тінь бере колір неба над нею: удень синьо-зелена, на заході синя, вночі майже чорна.
        '--shd': css(mix(mix(sk[0], g, 0.3), hex('#020806'), 0.6)), '--sky-shd': ((1 - cover) * 0.5 + 0.1).toFixed(2),
        '--shade': css(mix(mix(c.sk[0], grey[0], cover), hex('#020504'), 0.36)),
        // об'єм: тіньовий бік дерев, схилів і скель — з боку, протилежного сонцю; у хмарну погоду майже зникає
        '--sh-l': (clamp(0.5 + sinE * 2, 0, 1) * 0.4 * (1 - cover * 0.7)).toFixed(2),
        '--sh-r': (clamp(0.5 - sinE * 2, 0, 1) * 0.4 * (1 - cover * 0.7)).toFixed(2),
        // пора року: листя крон (взимку дерева голі), латаття й жовті глечики — наш знак, тож є завжди, крім зими (грудень — лютий)
        '--leaf': css(near(0.72, mix(sk[2], tone(g0, season(LEAF)), 0.76), 0.3)), '--leaf-o': (1 - 0.72 * wWin).toFixed(2),
        '--pine': css(near(0.8, mix(sk[2], gE, 0.8), 0.12)),
        '--pad-o': clamp((0.62 - wWin) * 5, 0, 1).toFixed(2), '--fl-o': clamp((0.62 - wWin) * 5, 0, 1).toFixed(2),
        '--tint': css(mix(mix(c.sk[2], grey[2], cover), g, 0.12)),
        '--w0': css(mix(mix(sk[2], sk[1], 0.3), mix(sk[2], hex('#ffffff'), 0.3), ice * 0.6)),
        '--w1': css(mix(mix(sk[1], g, 0.62), mix(sk[1], hex('#dfe8ea'), 0.35), ice * 0.55)),
        '--pad': css(mix(g, hex('#3f9e68'), 0.25 + 0.25 * t)),
        '--sky-orb': css(mix(pos.day ? c.orb : P.night.orb, sk[2], 0.1)),
        '--sky-halo': (Math.max(c.halo * 1.3, 0.5) * (1 - cover)).toFixed(3),
        '--sky-oo': clamp(1 - cover * 1.05, 0, 1).toFixed(2),
        '--sky-stars': (c.stars * (1 - clamp(cloud / 100, 0, 1) * 0.9)).toFixed(2),
        '--sky-cl': css(mix(sk[2], hex('#ffffff'), 0.12 + 0.38 * t)),
        '--sky-cirrus': (cloud > 55 ? 0 : 0.22).toFixed(2),
        '--sky-deck': clamp((cloud - 35) / 55, 0, 1).toFixed(2),
        // старі назви: смужка під шапкою й відблиск угорі сторінки
        '--sky1': css(sk[1]), '--sky2': css(sk[2]),
      },
      text: wx && wx.t != null && !(fake && fake.wx) ? (wx.t > 0 ? '+' : wx.t < 0 ? '−' : '') + Math.abs(Math.round(wx.t)) + '°, ' + word(wx.code, k)
        : fake && fake.wx ? WORD[fake.wx] || '' : '',
      place: (wx && wx.name) || '',
    };
  }

  /// Де стоїть світило на сцені: пейзаж розтягується на всю ширину й обрізається, тож рахуємо від справжніх розмірів.
  /// Сонце йде дугою зліва направо; уночі на його місці — місяць навпроти (проста модель, без фаз).
  function place() {
    const el = document.getElementById('sky');
    if (!el || !el.clientWidth) return;
    const W = el.clientWidth, H = el.clientHeight;
    // На телефоні малюнок нижчий за сцену (над ним — саме небо), тож масштаб беремо з малюнка, а не зі сцени.
    const svg = el.querySelector('.sky-art svg');
    const s = Math.max(W / ART_W, ((svg && svg.clientHeight) || H) / ART_H);
    const vis = W / s;                                        // скільки одиниць пейзажу видно в ширину
    const left = (ART_W - vis) / 2;
    const ha = pos.day ? pos.ha : Math.atan2(Math.sin(pos.ha - Math.PI), Math.cos(pos.ha - Math.PI));
    const alt = pos.day ? pos.alt : -pos.alt;
    // Ліворуч у небі стоїть заголовок, праворуч на широкому екрані — балачки: світило ходить у проміжку між ними,
    // якраз над мостом (на телефоні — правою половиною), щоб не сідати ні на текст, ні за панель.
    const narrow = el.dataset.anchor === 'narrow';
    const xu = left + vis * (narrow ? 0.8 + 0.08 * Math.sin(ha) : 0.52 + 0.16 * Math.sin(ha));
    el.style.setProperty('--ol', ((xu - left) * s).toFixed(1) + 'px');
    // Висота — частка від сьогоднішнього полудня: опівдні світило стоїть угорі сцени й улітку, і взимку (коли справжнє
    // сонце ледь піднімається над обрієм і весь день лежало б на пагорбах). Нижче обрію — тоне в далеких пагорбах.
    // Угорі сцени лежить шапка сайту — під неї світило не заходить.
    const hdr = (document.querySelector('header') || {}).offsetHeight || 56;
    const horizon = HORIZON * s, room = Math.max(60, H - horizon - hdr - 48);
    const up = alt >= 0 ? (alt / Math.max(12, pos.top || 40)) * room : alt * 3.3 * s;
    el.style.setProperty('--ob', Math.min(H - hdr - 36, horizon + up).toFixed(1) + 'px');
    el.style.setProperty('--rx', xu.toFixed(1));
    // Тіні на воді падають у бік від світила; що нижче воно стоїть, то вони довші.
    const lean = (-34 * Math.sin(ha)).toFixed(1);
    const long = (0.5 + 0.45 * (1 - clamp(alt / Math.max(12, pos.top || 40), 0, 1))).toFixed(2);
    el.querySelectorAll('[data-shd]').forEach((sh) => {
      const y = sh.dataset.shd;
      sh.setAttribute('transform', 'translate(0 ' + y + ') skewX(' + lean + ') scale(1 -' + long + ') translate(0 -' + y + ')');
    });
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
    const t = /^(\d{1,2}):(\d{2})$/.exec(s || ''), d = /^(\d{1,2})-(\d{1,2})$/.exec(day || '');
    if (!t && !d) return null;
    const at = new Date();
    if (d) at.setMonth(+d[1] - 1, +d[2]);
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
      if (q.get('sky') || q.get('wx') || q.get('day')) fake = { at: parseTime(q.get('sky'), q.get('day')), wx: WORD[q.get('wx')] ? q.get('wx') : null };
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
