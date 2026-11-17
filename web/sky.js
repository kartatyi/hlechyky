// 🌅 Небо (web/sky.js): сайт живе за справжнім часом — схід, день, захід, ніч — і за справжньою погодою.
// Де сонце, рахуємо самі (дата й координати, жодних сторонніх даних); хмари, дощ і сніг дає сервер (/api/weather:
// він сам питає Open-Meteo раз на чверть години, браузери людей туди не ходять). Нема погоди — небо просто ясне.
//
// Малює все CSS (static/polyva.css) і пейзаж static/obrii.svg: небо в три кольори, чотири шари землі, що тануть у
// димці, вода з відблиском. Звідси йдуть лише змінні на <html> — кольори (--sk0…--sk2, --l1…--l4, --w0, --w1, --pad),
// світило (--sky-orb, --sky-halo, --sky-oo), зорі й хмари (--sky-stars, --sky-cirrus, --sky-deck) — і два атрибути:
// data-sky (night | dawn | day | dusk) та data-wx (clear | cloud | fog | rain | snow | storm). Перераховуємо раз на
// п'ять хвилин і коли вкладка вертається; кольори не анімуються зовсім.
(function () {
  'use strict';
  let o = null;
  let wx = null;                 // остання відповідь /api/weather або null
  let wxAt = 0;
  let fake = null;               // перегляд: { at: Date | null, wx: 'rain' | … } — з адреси ?sky=06:10&wx=rain або HSky.preview()
  let pos = { alt: 0, ha: 0, day: true };
  const KYIV = { lat: 50.45, lon: 30.52 };
  const RAD = Math.PI / 180;
  // Пейзаж намальовано в цих одиницях (viewBox 0 90 2000 470); вода — на 204 одиниці вище за низ.
  const ART_W = 2000, ART_H = 470, HORIZON = 204;

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
  const BG = hex('#0f1f18');     // наш зелений: у нього сходить передній план, тож пейзаж виростає зі сторінки
  /// Чотири пори: небо згори донизу (верх, середина, обрій), зелень землі в цьому світлі, світило, сяйво довкола, зорі.
  const PAL = {
    night: { sk: ['#050a17', '#0b1830', '#1c2f4f'], g: '#0d1c16', orb: '#ece8d6', halo: 0.16, stars: 1 },
    dawn: { sk: ['#24366a', '#7d6a95', '#f5ab7e'], g: '#1d3d2e', orb: '#ffd9a6', halo: 0.5, stars: 0.15 },
    day: { sk: ['#2a80b5', '#69b5d2', '#cdebe2'], g: '#2e6c49', orb: '#fff7d6', halo: 0.42, stars: 0 },
    dusk: { sk: ['#231e52', '#95436e', '#f8904b'], g: '#1d3428', orb: '#ffb469', halo: 0.55, stars: 0.1 },
  };
  const P = {};
  for (const k of Object.keys(PAL)) P[k] = { sk: PAL[k].sk.map(hex), g: hex(PAL[k].g), orb: hex(PAL[k].orb), halo: PAL[k].halo, stars: PAL[k].stars };
  const blend = (a, b, t) => ({
    sk: a.sk.map((c, i) => mix(c, b.sk[i], t)), g: mix(a.g, b.g, t), orb: mix(a.orb, b.orb, t),
    halo: a.halo + (b.halo - a.halo) * t, stars: a.stars + (b.stars - a.stars) * t,
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
    const sk = c.sk.map((x, i) => mix(x, grey[i], cover));
    const g = mix(c.g, mix(c.g, hex('#222b2a'), 0.5), cover);
    const l4 = mix(g, BG, 0.74);
    pos = { alt: s.alt, ha: s.ha, top: s.top, day: s.alt > -4 };
    return {
      phase, kind: k,
      vars: {
        '--sk0': css(sk[0]), '--sk1': css(sk[1]), '--sk2': css(sk[2]),
        // далі від нас — ближче до кольору обрію (повітряна димка), ближче — до нашого зеленого
        '--l1': css(mix(sk[2], g, 0.42)), '--l2': css(mix(sk[2], g, 0.74)), '--l3': css(mix(g, BG, 0.38)), '--l4': css(l4), '--l4b': css(mix(l4, BG, 0.5)),
        '--w0': css(mix(sk[2], sk[1], 0.3)), '--w1': css(mix(sk[1], g, 0.62)),
        '--pad': css(mix(g, hex('#3f9e68'), 0.25 + 0.25 * t)),
        '--sky-orb': css(pos.day ? c.orb : P.night.orb),
        '--sky-halo': (c.halo * (1 - cover)).toFixed(3),
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
    const s = Math.max(W / ART_W, H / ART_H);
    const vis = W / s;                                        // скільки одиниць пейзажу видно в ширину
    const left = el.dataset.anchor === 'left' ? 0 : (ART_W - vis) / 2;
    const ha = pos.day ? pos.ha : Math.atan2(Math.sin(pos.ha - Math.PI), Math.cos(pos.ha - Math.PI));
    const alt = pos.day ? pos.alt : -pos.alt;
    // Ліворуч у небі стоїть заголовок, праворуч на широкому екрані — балачки: світило ходить у проміжку між ними
    // (на телефоні — правою половиною), щоб не сідати ні на текст, ні за панель.
    const narrow = el.dataset.anchor === 'left';
    const xu = left + vis * (narrow ? 0.88 + 0.05 * Math.sin(ha) : 0.52 + 0.16 * Math.sin(ha));
    el.style.setProperty('--ol', ((xu - left) * s).toFixed(1) + 'px');
    // Висота — частка від сьогоднішнього полудня: опівдні світило стоїть угорі сцени й улітку, і взимку (коли справжнє
    // сонце ледь піднімається над обрієм і весь день лежало б на пагорбах). Нижче обрію — ховається за землю.
    const horizon = (HORIZON - 14) * s, room = Math.max(60, H - horizon - 64);
    const up = alt >= 0 ? (alt / Math.max(12, pos.top || 40)) * room : alt * 3.3 * s;
    el.style.setProperty('--ob', Math.min(H - 44, horizon + up).toFixed(1) + 'px');
    el.style.setProperty('--rx', xu.toFixed(1));
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

  const parseTime = (s) => {
    const t = /^(\d{1,2}):(\d{2})$/.exec(s || '');
    if (!t) return null;
    const at = new Date();
    at.setHours(+t[1], +t[2], 0, 0);
    return at;
  };

  /// Пейзаж — окремий файл: вставляємо його в сцену розміткою (щоб кольори брались зі змінних), раз на сторінку.
  function mount() {
    const el = document.getElementById('sky');
    if (!el || el.dataset.ready) return;
    el.dataset.ready = '1';
    const narrow = window.matchMedia('(max-width: 900px)');
    const anchor = () => {
      // На телефоні показуємо лівий край пейзажу — там очерет, тополі й латаття; середина без них порожня.
      el.dataset.anchor = narrow.matches ? 'left' : 'mid';
      const svg = el.querySelector('svg');
      if (svg) svg.setAttribute('preserveAspectRatio', narrow.matches ? 'xMinYMax slice' : 'xMidYMax slice');
      place();
    };
    fetch('/static/obrii.svg').then((r) => (r.ok ? r.text() : '')).then((t) => {
      if (!t) return;
      el.querySelector('.sky-art').innerHTML = t;
      anchor();
    }).catch(() => { /* без пейзажу лишається небо-градієнт */ });
    narrow.addEventListener?.('change', anchor);
    if (window.ResizeObserver) new ResizeObserver(place).observe(el);
    anchor();
  }

  window.HSky = {
    init(opts) {
      o = opts || {};
      const q = new URLSearchParams(location.search);
      if (q.get('sky') || q.get('wx')) fake = { at: parseTime(q.get('sky')), wx: WORD[q.get('wx')] ? q.get('wx') : null };
      mount();
      paint();
      setInterval(() => { if (!document.hidden) { paint(); load(false); } }, 5 * 60000);
      document.addEventListener('visibilitychange', () => { if (!document.hidden) { paint(); load(false); } });
    },
    /// Після /api/me і коли людина змінила район у «Тривозі».
    ready() { load(true); },
    /// Для перевірок і показу: HSky.preview({ time: '21:40', wx: 'snow' }); без аргументів — назад до справжнього.
    preview(p) {
      fake = p ? { at: parseTime(p.time), wx: p.wx && WORD[p.wx] ? p.wx : null } : null;
      paint();
    },
    /// Сцена з'явилась чи змінила розмір (зайшли на головну) — розставити світило й підписи ще раз.
    repaint() { last = ''; paint(); },
  };
})();
