// 🌅 Небо (web/sky.js): сайт живе за справжнім часом — схід, день, захід, ніч — і за справжньою погодою.
// Де сонце, рахуємо самі (дата й координати, жодних сторонніх даних); хмари, дощ і сніг дає сервер (/api/weather:
// він сам питає Open-Meteo раз на чверть години, браузери людей туди не ходять). Нема погоди — небо просто ясне.
//
// Назовні — лише кілька змінних на <html>: --sky1 і --sky2 (верх і низ неба), --sky-orb (сонце чи місяць), --sky-ox,
// --sky-oy (де воно), --sky-oo (наскільки видно); і два атрибути: data-sky (night | dawn | day | dusk) та data-wx
// (clear | cloud | fog | rain | snow | storm). Малює все CSS (static/polyva.css): сцену «Обрій» на головній і тонку
// смужку неба під шапкою. Перераховуємо раз на п'ять хвилин і коли вкладка вертається — анімацій кольору нема зовсім.
(function () {
  'use strict';
  let o = null;
  let wx = null;                 // остання відповідь /api/weather або null
  let wxAt = 0;
  let fake = null;               // перегляд: { at: Date | null, wx: 'rain' | … } — з адреси ?sky=06:10&wx=rain або HSky.preview()
  const KYIV = { lat: 50.45, lon: 30.52 };
  const RAD = Math.PI / 180;

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
    return { alt: alt / RAD, ha };
  }

  // ---------------------------------------------------------------------------------------------
  // кольори
  // ---------------------------------------------------------------------------------------------

  const hex = (s) => [1, 3, 5].map((i) => parseInt(s.slice(i, i + 2), 16));
  const mix = (a, b, t) => a.map((x, i) => Math.round(x + (b[i] - x) * t));
  const css = (c) => 'rgb(' + c.join(' ') + ')';
  const PAL = {
    night: [hex('#0d1830'), hex('#172a3d')],
    dawn: [hex('#44527e'), hex('#e9a27c')],
    day: [hex('#3f9fc4'), hex('#a4d6d4')],
    dusk: [hex('#5b3a7a'), hex('#f08a4b')],
  };
  const GREY_NIGHT = [hex('#20262d'), hex('#2a3138')], GREY_DAY = [hex('#56626c'), hex('#8a959a')];
  const ORB = { day: hex('#fff6cf'), low: hex('#ffc27d'), moon: hex('#e9e6d2') };
  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));

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
    // −10° і нижче — ніч; до +4° — сутінки (уранці рожево-персикові, увечері жовтогарячі з бузком); вище — день.
    const t = clamp((s.alt + 10) / 14, 0, 1);
    const tw = morning ? PAL.dawn : PAL.dusk;
    let c = t < 0.5 ? [mix(PAL.night[0], tw[0], t * 2), mix(PAL.night[1], tw[1], t * 2)]
      : [mix(tw[0], PAL.day[0], (t - 0.5) * 2), mix(tw[1], PAL.day[1], (t - 0.5) * 2)];
    const phase = t <= 0.12 ? 'night' : t >= 0.92 ? 'day' : morning ? 'dawn' : 'dusk';
    // Погода: хмарність тягне небо до сірого, дощ і сніг — ще трохи.
    const k = fake && fake.wx ? fake.wx : wx ? kind(wx.code) : 'clear';
    const cover = fake && fake.wx ? (fake.wx === 'clear' ? 0 : 85) : wx && wx.cover != null ? wx.cover : k === 'clear' ? 0 : 70;
    const grey = [mix(GREY_NIGHT[0], GREY_DAY[0], t), mix(GREY_NIGHT[1], GREY_DAY[1], t)];
    const g = clamp(cover / 100, 0, 1) * (k === 'rain' || k === 'snow' || k === 'storm' || k === 'fog' ? 0.85 : 0.6);
    c = [mix(c[0], grey[0], g), mix(c[1], grey[1], g)];
    // Сонце йде дугою зліва направо; уночі на його місці — місяць навпроти (проста модель, без фаз).
    const dayOrb = s.alt > -3;
    const ha = dayOrb ? s.ha : Math.atan2(Math.sin(s.ha - Math.PI), Math.cos(s.ha - Math.PI));
    const alt = dayOrb ? s.alt : -s.alt;
    const orb = !dayOrb ? ORB.moon : mix(ORB.low, ORB.day, clamp(s.alt / 14, 0, 1));
    return {
      phase, kind: k, c1: css(c[0]), c2: css(c[1]), orb: css(orb),
      ox: (50 + 42 * Math.sin(ha)).toFixed(1) + '%',
      oy: clamp(72 - (alt / 58) * 60, 12, 74).toFixed(1) + '%',
      oo: (1 - clamp(g * 1.15, 0, 0.92)).toFixed(2),
      text: wx && wx.t != null && !(fake && fake.wx) ? (wx.t > 0 ? '+' : wx.t < 0 ? '−' : '') + Math.abs(Math.round(wx.t)) + '° · ' + word(wx.code, k)
        : fake && fake.wx ? WORD[fake.wx] || '' : '',
      place: (wx && wx.name) || '',
    };
  }

  let last = '';
  function paint() {
    const v = compute();
    const sig = JSON.stringify(v);
    if (sig === last) return;
    last = sig;
    const r = document.documentElement;
    r.style.setProperty('--sky1', v.c1);
    r.style.setProperty('--sky2', v.c2);
    r.style.setProperty('--sky-orb', v.orb);
    r.style.setProperty('--sky-ox', v.ox);
    r.style.setProperty('--sky-oy', v.oy);
    r.style.setProperty('--sky-oo', v.oo);
    r.dataset.sky = v.phase;
    r.dataset.wx = v.kind;
    document.querySelectorAll('[data-sky-text]').forEach((el) => {
      el.textContent = v.text;
      el.title = v.place ? 'Погода: ' + v.place : '';
      el.hidden = !v.text;
    });
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

  /// ?sky=06:10&wx=rain — глянути на небо о будь-якій порі й за будь-якої погоди, не чекаючи вечора.
  function fromUrl() {
    const q = new URLSearchParams(location.search);
    const t = /^(\d{1,2}):(\d{2})$/.exec(q.get('sky') || '');
    const w = q.get('wx');
    if (!t && !w) return;
    let at = null;
    if (t) { at = new Date(); at.setHours(+t[1], +t[2], 0, 0); }
    fake = { at, wx: w && WORD[w] ? w : null };
  }

  window.HSky = {
    init(opts) {
      o = opts || {};
      fromUrl();
      paint();
      setInterval(() => { if (!document.hidden) { paint(); load(false); } }, 5 * 60000);
      document.addEventListener('visibilitychange', () => { if (!document.hidden) { paint(); load(false); } });
    },
    /// Після /api/me і коли людина змінила район у «Тривозі».
    ready() { load(true); },
    /// Для перевірок і показу: HSky.preview({ time: '21:40', wx: 'snow' }); без аргументів — назад до справжнього.
    preview(p) {
      if (!p) fake = null;
      else {
        let at = null;
        const t = /^(\d{1,2}):(\d{2})$/.exec(p.time || '');
        if (t) { at = new Date(); at.setHours(+t[1], +t[2], 0, 0); }
        fake = { at, wx: p.wx && WORD[p.wx] ? p.wx : null };
      }
      paint();
    },
    /// Намалювали нову сцену (головна) — поставити їй підпис і кольори ще раз.
    repaint() { last = ''; paint(); },
  };
})();
