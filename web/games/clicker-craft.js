/*
  Ремесло Гончарного кола (docs/games/specs/clicker-v7.md, пакет A). Частина ядра clicker.js.

  Що тут:
  1) силуети п'ятнадцяти виробів (api.wareSvg) — ними малюють коло, полицю, комору, альбом, горно й цех;
  2) виріб на колі, що росте від кліків: грудка → центрування → відкривання → витягування → форма. Сервер рахує
     роботу (view.craft.work/need), клієнт лише передбачає її між видами: + кліки, що ще не полетіли, + підмайстри;
  3) сирці на полиці над колом (view.craft.rack): мокрі темніші, висохлі світлі;
  4) смуга «Шлях виробу» під сценою (v8): чотири кроки коло → сушарня → горно → комора й один рядок «Далі» з
     єдиною кнопкою, яка робить наступний крок (замість рядка ремесла, банера цілі й окремих підказок);
  5) вкладка «Комора»: вироби з горна, базар (Act('bazaar')) — усе, лише звичайні або все, крім дзвінких.
*/
(() => {
  const PROGRESS_STEPS = 48;              // стільки разів за виріб перемальовуємо силует на колі — не щокадру

  // ---------- силуети ----------

  /// Профільні вироби: півширина на висотах [y, w] від дна (y = 86) до вінець. Центр — x = 50.
  const PROFILE = {
    pot: [[86, 12], [83, 18], [74, 23], [62, 23], [53, 19], [48, 16], [45, 17.5], [43, 18]],
    bowl: [[86, 10], [84, 15], [80, 24], [75, 31], [71, 34], [69, 34.5]],
    jug: [[86, 11], [81, 17], [70, 21], [58, 19], [49, 11], [42, 7.5], [36, 7], [32, 8], [30, 9]],
    makitra: [[86, 11], [81, 17], [72, 24], [63, 29], [58, 31], [55, 33], [53, 33]],
    dish: [[86, 12], [85, 20], [82, 31], [79, 37], [77, 39]],
    candle: [[86, 20], [83, 19], [81, 6], [66, 4.5], [60, 5], [56, 12], [52, 13], [50, 12]],
    barrel: [[86, 12], [80, 19], [70, 24], [58, 24], [48, 20], [41, 12], [38, 6], [33, 5.5], [31, 6.5]],
    // Дев'яте оновлення: кухоль — простий високий циліндр (усе впізнавання дає вушко в DETAIL), тиква — гарбуз
    // із довгою шийкою.
    kukhol: [[86, 13], [84, 15.5], [74, 16], [60, 16.5], [50, 17], [47, 17.5]],
    tykva: [[86, 10], [83, 16], [77, 23], [70, 26.5], [63, 26], [56, 21], [50, 13], [45, 8.5], [40, 7.5], [36, 8], [34, 9.5]],
  };
  /// Фігурні вироби: готовий силует — окремий шлях; поки ліпиться — округла «заготовка» того ж розміру.
  const FIGURE = {
    whistle: {
      path: 'M36 86l2-9h24l2 9zM34 76c-6-10-2-24 10-27 0-8 5-14 12-14 5 0 8 4 8 9l7 2-7 3c1 7-2 12-7 15 6-3 12-10 14-19 7 8 6 23-4 29-4 2-9 2-13 2z',
      stand: [[86, 14], [78, 18], [66, 20], [54, 16], [44, 10], [38, 7]],
    },
    tile: {
      path: 'M22 28h56v58H22z',
      stand: [[86, 26], [70, 27], [50, 27], [32, 26], [28, 25]],
    },
    kumanets: {
      path: 'M39 86l3-9h16l3 9zM50 27a25 25 0 1 1-.01 0zM50 42a10 10 0 1 0 .01 0zM45 17h10v11H45zM66 33l10-9 4 4-10 9z',
      evenodd: true,
      stand: [[86, 12], [78, 20], [64, 25], [50, 24], [38, 16], [30, 8], [22, 6]],
    },
    ram: {
      path: 'M32 86V74h7v12zM61 86V74h7v12zM22 60c0-13 9-18 22-18h16c9 0 14 4 17 10l9-2c-1 9-6 13-11 14 0 8-7 12-14 12H36c-9 0-14-6-14-16z',
      stand: [[86, 20], [76, 24], [64, 26], [52, 22], [44, 16], [40, 10]],
    },
    lion: {
      path: 'M30 86V61c0-11 6-17 12-20-5-4-7-9-7-14 0-9 7-15 15-15s15 6 15 15c0 5-2 10-7 14 6 3 12 9 12 20v25zM70 80c8-2 12-8 10-16 4 3 5 10 0 16z',
      stand: [[86, 20], [72, 22], [58, 20], [44, 14], [32, 15], [18, 12]],
    },
    // Плесканець — пласка дорожня посудина: круглий бік анфас, коротке горло й два вушка при плечах.
    pleskanets: {
      path: 'M50 33a26 26 0 1 1-.01 0zM44 26h12v11H44zM42 20h16v7H42zM26 41l-8-5 2-4 9 5zM74 41l8-5-2-4-9 5z',
      stand: [[86, 18], [74, 24], [60, 26], [46, 20], [36, 10], [30, 8]],
    },
  };

  /// Деталі поверх силуету готового виробу: ручки, носики, отвори, рельєф.
  const DETAIL = {
    pot: '<path d="M32 50q-6 2-5 7M68 50q6 2 5 7" stroke="rgba(0,0,0,.32)" stroke-width="2.4" fill="none" stroke-linecap="round"/>',
    jug: '<path d="M58 40c11-2 15 6 12 14-2 5-6 7-10 8" stroke="var(--clkw-body)" stroke-width="4" fill="none" stroke-linecap="round"/>'
      + '<path d="M58 40c11-2 15 6 12 14-2 5-6 7-10 8" stroke="rgba(0,0,0,.25)" stroke-width="1" fill="none"/>',
    makitra: '<path d="M18 53h64" stroke="rgba(0,0,0,.28)" stroke-width="2.2"/>',
    candle: '<ellipse cx="50" cy="50.5" rx="12" ry="2.4" fill="rgba(0,0,0,.3)"/><path d="M50 36v12" stroke="#f4efe3" stroke-width="2.2"/>'
      + '<path class="clkw-flame" d="M50 27c-3 3-3 7 0 9 3-2 3-6 0-9z" fill="#f4c542"/>',
    barrel: '<path d="M27 62h46M29 74h42" stroke="rgba(0,0,0,.26)" stroke-width="1.6"/><path d="M64 44l9-5 2 3-8 6z" fill="var(--clkw-body)" stroke="rgba(0,0,0,.3)" stroke-width=".8"/>',
    tile: '<rect x="28" y="34" width="44" height="46" rx="2" fill="none" stroke="rgba(0,0,0,.3)" stroke-width="2"/>'
      + '<circle cx="50" cy="57" r="9" fill="none" stroke="rgba(0,0,0,.25)" stroke-width="1.6"/>',
    kumanets: '<circle cx="50" cy="52" r="10" fill="none" stroke="rgba(0,0,0,.35)" stroke-width="1.2"/>',
    whistle: '<path d="M60 38l3-5 2 5 3-4 1 5" fill="#c62f25"/><circle cx="60" cy="45" r="1.3" fill="#1b1310"/>',
    ram: '<path d="M68 52c6-4 11 1 8 6-2 3-6 2-6-1" stroke="rgba(0,0,0,.4)" stroke-width="2" fill="none"/><circle cx="78" cy="55" r="1.1" fill="#1b1310"/>'
      + '<path d="M30 56c3-3 6-3 8 0M40 52c3-3 6-3 8 0M50 56c3-3 6-3 8 0" stroke="rgba(255,255,255,.28)" stroke-width="1.4" fill="none"/>',
    lion: '<circle cx="44" cy="26" r="1.6" fill="#1b1310"/><circle cx="56" cy="26" r="1.6" fill="#1b1310"/><path d="M46 34q4 3 8 0" stroke="#1b1310" stroke-width="1.2" fill="none"/>'
      + '<path d="M50 14c-10 0-17 6-17 14M50 14c10 0 17 6 17 14" stroke="rgba(0,0,0,.25)" stroke-width="3" fill="none"/>',
    // Вушко кухля — те саме, що й ручка глечика: спершу тілом, потім тонкою тінню, щоб читалось на будь-якій глині.
    kukhol: '<path d="M66 54c12 1 13 19 0 21" stroke="var(--clkw-body)" stroke-width="5" fill="none" stroke-linecap="round"/>'
      + '<path d="M66 54c12 1 13 19 0 21" stroke="rgba(0,0,0,.28)" stroke-width="1" fill="none"/>'
      + '<path d="M34 60h32" stroke="rgba(0,0,0,.22)" stroke-width="1.8"/>',
    tykva: '<path d="M37 78q-5-13 2-26M63 78q5-13-2-26" stroke="rgba(0,0,0,.2)" stroke-width="1.6" fill="none"/>'
      + '<ellipse cx="50" cy="45" rx="8.6" ry="2" fill="none" stroke="rgba(0,0,0,.3)" stroke-width="2"/>',
    pleskanets: '<circle cx="50" cy="59" r="18" fill="none" stroke="rgba(0,0,0,.26)" stroke-width="1.8"/>'
      + '<circle cx="50" cy="59" r="8" fill="none" stroke="rgba(0,0,0,.22)" stroke-width="1.4"/>',
  };

  /// Світлотінь заготовки на колі (одна на сторінку: id сталий).
  const SHADE = '<defs><linearGradient id="clkw-shade" x1="0" y1="0" x2="1" y2="0">'
    + '<stop offset="0" stop-color="#000" stop-opacity=".42"/><stop offset=".3" stop-color="#fff" stop-opacity=".2"/>'
    + '<stop offset=".45" stop-color="#fff" stop-opacity=".05"/><stop offset=".8" stop-color="#000" stop-opacity=".12"/>'
    + '<stop offset="1" stop-color="#000" stop-opacity=".5"/></linearGradient></defs>';

  /// Гладкий силует з профілю: права сторона знизу вгору, ліва — дзеркально, вінця — плоскі.
  function profilePath(pts) {
    const r = pts.map(([y, w]) => [50 + w, y]);
    const l = pts.slice().reverse().map(([y, w]) => [50 - w, y]);
    const smooth = (arr) => {
      let d = '';
      for (let i = 1; i < arr.length; i++) {
        const [x0, y0] = arr[i - 1];
        const [x1, y1] = arr[i];
        d += ' Q' + x0.toFixed(1) + ' ' + y0.toFixed(1) + ' ' + ((x0 + x1) / 2).toFixed(1) + ' ' + ((y0 + y1) / 2).toFixed(1);
      }
      const last = arr[arr.length - 1];
      return d + ' L' + last[0].toFixed(1) + ' ' + last[1].toFixed(1);
    };
    return 'M50 ' + pts[0][0] + ' L' + r[0][0].toFixed(1) + ' ' + r[0][1] + smooth(r) + ' L' + l[0][0].toFixed(1) + ' ' + l[0][1].toFixed(1)
      + smooth(l) + ' Z';
  }

  /// Півширина профілю на частці висоти t (0 — дно, 1 — вінця), лінійно між точками.
  function widthAt(pts, t) {
    const bottom = pts[0][0];
    const top = pts[pts.length - 1][0];
    const y = bottom - t * (bottom - top);
    for (let i = 1; i < pts.length; i++) {
      const [ya, wa] = pts[i - 1];
      const [yb, wb] = pts[i];
      if (y <= ya && y >= yb) return wa + (wb - wa) * ((ya - y) / (ya - yb || 1));
    }
    return pts[pts.length - 1][1];
  }

  /// Профіль будь-якої висоти й форми з N точок: так грудку, циліндр і готовий виріб можна змішувати.
  function resample(fn, height, n = 10) {
    const out = [];
    for (let i = 0; i < n; i++) {
      const t = i / (n - 1);
      out.push([86 - t * height, fn(t)]);
    }
    return out;
  }

  const targetOf = (ware) => PROFILE[ware] || (FIGURE[ware] && FIGURE[ware].stand) || PROFILE.pot;
  const heightOf = (pts) => pts[0][0] - pts[pts.length - 1][0];

  /// Форма на колі за часткою роботи p (0…1): грудка → центрування → відкривання → витягування → форма.
  function formingProfile(ware, p) {
    const target = targetOf(ware);
    const H = heightOf(target);
    const avg = target.reduce((s, [, w]) => s + w, 0) / target.length;
    const lump = (t, s) => Math.max(0.5, 17 * s * Math.sqrt(Math.max(0, 1 - t * t * 0.92)));
    if (p < 0.2) {
      const s = 0.75 + p * 1.25;
      return { pts: resample((t) => lump(t, s), 16 * s), open: false };
    }
    if (p < 0.42) {
      const k = (p - 0.2) / 0.22;
      const h = 16 + 6 * k;
      return { pts: resample((t) => lump(t, 1) * (1 - k) + 15 * k, h), open: k > 0.3 };
    }
    if (p < 0.7) {
      const k = (p - 0.42) / 0.28;
      const h = 22 + (H * 0.92 - 22) * k;
      const w = 15 + (avg - 15) * k;
      // Стінки під пальцями не стоять рівно: трохи пузата посередині й звужена до вінець — не відро, а заготовка.
      return { pts: resample((t) => w * (1 + 0.1 * Math.sin(Math.PI * t) - 0.12 * t * t), h), open: true };
    }
    const k = Math.min(1, (p - 0.7) / 0.3);
    const h = H * 0.92 + H * 0.08 * k;
    const w = 15 + (avg - 15);
    return { pts: resample((t) => w * (1 - k) + widthAt(target, t) * k, h), open: true };
  }

  /// Відблиск і сяйво за якістю: дзвінкий — золота обводка й іскорка, добрий — м'який полиск.
  function qualityMarks(q) {
    if (q >= 3) return '<path d="M36 44c-2 6-2 16 1 22" stroke="rgba(255,244,200,.55)" stroke-width="2.4" fill="none" stroke-linecap="round"/>'
      + '<path d="M77 20l2 5 5 2-5 2-2 5-2-5-5-2 5-2z" fill="#ffe28a"/>';
    if (q === 2) return '<path d="M37 46c-2 5-2 13 1 18" stroke="rgba(255,255,255,.35)" stroke-width="2" fill="none" stroke-linecap="round"/>';
    return '';
  }

  /// SVG виробу. opts: style (розпис), clay (колір глини для простого/сирця), quality (0 — сирець), raw (сирець:
  /// без розпису й полиску), dry (висохлий сирець світліший), progress (0…1 — ще ліпиться), slot (стале id clipPath),
  /// cls, wrap=false (лише вміст для чужого <svg>).
  function wareSvg(api, ware, o) {
    o = o || {};
    const STYLE = api.STYLE;
    const style = o.raw ? '' : (o.style || '');
    const s = STYLE[style] || STYLE[''];
    const clay = o.clay || '';
    const body = style ? s.body : (clay || 'var(--clay)');
    const id = 'clkw-' + (o.slot || ware + '-' + style + '-' + (o.quality || 0) + (o.raw ? 'r' : '') + (o.dry ? 'd' : ''));
    const forming = o.progress != null && o.progress < 1;
    let shape;
    let evenodd = false;
    if (forming) {
      const f = formingProfile(ware, Math.max(0, o.progress));
      shape = profilePath(f.pts);
    } else if (FIGURE[ware]) {
      shape = FIGURE[ware].path;
      evenodd = !!FIGURE[ware].evenodd;
    } else {
      shape = profilePath(PROFILE[ware] || PROFILE.pot);
    }
    const rule = evenodd ? ' fill-rule="evenodd" clip-rule="evenodd"' : '';
    const wet = o.raw && !o.dry;
    let g = '<g class="clkw' + (o.raw ? ' raw' : '') + (wet ? ' wet' : '') + (forming ? ' forming' : '') + '" style="--clkw-body:' + body + '">'
      + (forming ? SHADE : '')
      + '<clipPath id="' + id + '"><path d="' + shape + '"' + rule + '/></clipPath>'
      + '<path d="' + shape + '" fill="' + body + '"' + rule + '/>';
    if (!forming && !o.raw && style) {
      // Орнамент розписів намальований для глечика (пояс 36…62): для низьких і широких виробів зсуваємо його до пуза.
      const shift = PROFILE[ware] ? Math.round(86 - heightOf(PROFILE[ware]) * 0.55 - 49) : 0;
      g += '<g clip-path="url(#' + id + ')"><g transform="translate(0 ' + shift + ')' + (ware === 'bowl' || ware === 'dish' ? ' scale(1 .7) translate(0 32)' : '') + '">'
        + s.decor + '</g></g>';
    }
    if (forming) {
      const f = formingProfile(ware, Math.max(0, o.progress));
      const top = f.pts[f.pts.length - 1];
      if (f.open) g += '<ellipse cx="50" cy="' + top[0].toFixed(1) + '" rx="' + Math.max(1, top[1] - 2.5).toFixed(1) + '" ry="2.4" fill="rgba(0,0,0,.35)"/>';
      // Об'єм: тінь по краях і мокрий відблиск ліворуч від центру — інакше заготовка читається плоским прямокутником.
      g += '<g clip-path="url(#' + id + ')"><rect x="10" y="10" width="80" height="80" fill="url(#clkw-shade)"/>'
        // Борозни від пальців — тонкі дуги, що біжать довкола (коло крутиться), а не рівні смуги.
        + '<g stroke="rgba(255,255,255,.1)" stroke-width=".7" fill="none">'
        + '<path d="M22 81q28 3 56-1M22 74q28 3 56-1M22 67q28 3 56-1M22 60q28 3 56-1M22 53q28 3 56-1M22 46q28 3 56-1M22 39q28 3 56-1"/></g></g>';
    } else if (DETAIL[ware]) {
      g += DETAIL[ware];
    }
    g += '<path d="' + shape + '" fill="none" stroke="rgba(0,0,0,.3)" stroke-width="1"' + rule + '/>';
    if (!o.raw && !forming) g += qualityMarks(o.quality || 1);
    g += '</g>';
    if (o.wrap === false) return g;
    return '<svg class="' + (o.cls || '') + '" viewBox="10 8 80 82" aria-hidden="true">' + g + '</svg>';
  }

  // ---------- стан і числа ----------

  const styleName = (st, key) => {
    if (!key) return 'простий';
    const s = (st.styleList || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const QUALITY = ['', 'звичайний', 'добрий', 'дзвінкий', 'розкішний'];
  const STARS = ['', '★', '★★', '★★★', '👑'];

  /// Відкладене (v12, вид craft.items[].kept = [толока, замовлення, майстерштук] або null): скільки з цих штук чекають
  /// Толока, замовлення сіл і гостей та майстерштук — так само, як їх тримає лад і віз (сервер, LadKept).
  const keptOf = (it) => (it.kept ? Math.min(it.n, (it.kept[0] || 0) + (it.kept[1] || 0) + (it.kept[2] || 0)) : 0);
  function keptSum(items, q) {
    const sum = [0, 0, 0];
    for (const it of items) if (it.q <= q && it.kept) for (let i = 0; i < 3; i++) sum[i] += it.kept[i] || 0;
    return sum;
  }
  function keptText(k) {
    const parts = [];
    if (k[0]) parts.push('Толока ' + k[0]);
    if (k[1]) parts.push('замовлення ' + k[1]);
    if (k[2]) parts.push('майстерштук ' + k[2]);
    return parts.join(' · ');
  }
  /// Друге «Точно?» в продажу гуртом: якщо підуть і відкладені — так і кажемо (сервер поки продає їх разом з усім).
  // Сервер (v12) за замовчуванням лишає відкладене під Толоку й замовлення: «Продати все» з «Далі» так і каже.
  const sellArm = (kept) => (kept > 0 ? 'Точно? 🔒 ' + kept + ' відкладених лишаться' : 'Точно все? Ще раз');
  /// Скільки тримається друге «Точно?» (QA-2: за 3 с не встигали).
  const ARM_MS = 4500;

  function wareName(st, key) {
    const c = st.craft;
    const w = c && c.wares.find((x) => x.key === key);
    return w ? w.name : key;
  }

  /// Вироби для всіх частин (ремесло, горно, ярмарок, альбом, цех) — у тих самих рядках, що й раніше: назва й
  /// «відкриється на» — з каталогу, стан (відкритий, робота, обпалено, ціна) — з виду. Десяте оновлення (§10) винесло
  /// незмінне з виду, що летить щопачки кліків. Старий сервер шле рядки цілими — беремо як є; без каталогу (перші
  /// миті після F5, поки ядро його допитується) назвою служить ключ.
  function waresOf(st, list) {
    if (!list.length || list[0].name != null) return list;
    const cat = new Map(((st.catalog && st.catalog.wares) || []).map((w) => [w.key, w]));
    return list.map((w) => {
      const k = cat.get(w.key) || {};
      return { key: w.key, name: k.name || w.key, unlock: k.unlock || 0, open: !!w.open, need: w.need || 1, fired: w.fired || 0, value: w.value || 0 };
    });
  }

  /// Прокачка ремесла: назва й опис — з каталогу, рівень зі стелею, ціна наступного й «зараз: …» — з виду.
  function upsOf(st, list) {
    if (!list.length || list[0].name != null) return list;
    const cat = new Map(((st.catalog && st.catalog.craftUps) || []).map((u) => [u.key, u]));
    return list.map((u) => {
      const k = cat.get(u.key) || {};
      return { key: u.key, name: k.name || u.key, desc: k.desc || '', level: u.level || 0, max: u.max || 0, price: u.price || 0, now: u.now || '' };
    });
  }

  /// Скільки роботи вже є просто зараз: серверне число + кліки, що ще не полетіли або летять, + підмайстри.
  function workNow(st) {
    const c = st.craft;
    if (!c) return 0;
    const clicks = st.hands.length + st.inflight;
    const idle = c.rackFull ? 0 : (c.apprentice * Math.max(0, Date.now() - st.craftAt)) / 1000;
    return c.work + clicks + idle;
  }

  // ---------- малювання ----------

  function paintWheel(st, api) {
    const c = st.craft;
    if (!c || !st.jugBox) return;
    const need = Math.max(1, c.need);
    let w = workNow(st);
    // Передбачили готовий виріб — ефект один раз, а на колі вже нова грудка (якщо сушарня має місце).
    const done = Math.floor(w / need);
    if (done > st.craftDone) {
      st.craftDone = done;
      if (st.craftRackFree > 0) {
        st.craftRackFree--;
        st.craftFxAt = Date.now();
        const name = wareName(st, c.ware);
        api.popAt(st, '🏺 ' + name.toLowerCase() + ' — на сушарню', 'big', 50, 30);
        api.sparks(st, st.fx, 10, false, 50, 62);
        api.sfx('done');
      }
    }
    const full = st.craftRackFree <= 0 && w >= need;
    const p = full ? 1 : (w % need) / need;
    const step = full ? PROGRESS_STEPS : Math.floor(p * PROGRESS_STEPS);
    const sig = c.ware + '|' + step + '|' + st.clayBody;
    if (st.jugBox._craft !== sig) {
      st.jugBox._craft = sig;
      // Виріб на колі: основа на центрі круга. Готовий (сушарня повна) — фінальний силует простого виробу.
      const inner = wareSvg(api, c.ware, {
        progress: full ? 1 : step / PROGRESS_STEPS, raw: true, dry: false, clay: st.clayBody, slot: 'wheel-craft', wrap: false,
      });
      st.jugBox.innerHTML = '<g transform="translate(50 70) scale(.78) translate(-50 -86)">' + inner + '</g>';
    }
    if (st.craftUi) {
      // Смужку кроку «коло» доводимо щокадру (решту смуги вистачає малювати раз на slow) — масштабом, а не шириною:
      // transform не чіпає розкладки, а ширина щокадру перераховувала б її 60 разів на секунду (десяте оновлення, §10).
      setBar(st.craftUi.wheelBar, p);
      if (st.craftUi.full !== full) { st.craftUi.full = full; st.craftUi.el.classList.toggle('full', full); }
    }
  }

  /// Смужка прогресу кроком у тисячну: пишемо стиль лише тоді, коли число справді зрушило.
  function setBar(el, frac) {
    if (!el) return;
    const k = Math.max(0, Math.min(1000, Math.round(frac * 1000)));
    if (el._k === k) return;
    el._k = k;
    el.style.transform = 'scaleX(' + k / 1000 + ')';
  }

  /// Сирці на полиці над колом: до десяти, решта — «+N». Мокрі темніші; висохлі — світлі й чекають горна.
  function paintShelf(st, api) {
    const c = st.craft;
    if (!c || !st.shelfJugs) return;
    const now = api.serverNow(st);
    const shown = c.rack.slice(0, 10);
    const sig = shown.map((r) => r.ware + (r.clay || '') + (r.thin ? '*' : '') + (r.dryAt <= now ? 'd' : 'w')).join(',') + '|' + c.rack.length;
    if (st.shelfJugs._craft === sig) return;
    st.shelfJugs._craft = sig;
    let s = shown.map((r, i) => '<span class="clkw-rack' + (r.dryAt <= now ? ' dry' : '') + (r.thin ? ' thin' : '') + '" title="' + wareName(st, r.ware)
      + (r.thin ? ' — тонкий з каоліну, з горна вийде розкішним' : '') + (r.dryAt <= now ? ' — сухий, чекає горна' : ' — сохне') + '">'
      + wareSvg(api, r.ware, { raw: true, dry: r.dryAt <= now, clay: clayBody(st, r.clay), slot: 'rack-' + i }) + '</span>').join('');
    if (c.rack.length > shown.length) s += '<span class="clkw-more">+' + (c.rack.length - shown.length) + '</span>';
    if (!c.rack.length) s = '<span class="clkw-empty">сушарня порожня</span>';
    st.shelfJugs.innerHTML = s;
  }

  const clayBody = (st, key) => {
    const c = (st.clays || []).find((x) => x.key === key);
    return (c && c.body) || '';
  };

  // ---------- смуга «Шлях виробу» й рядок «Далі» ----------

  /// Стан горна беремо просто зі спільного st: горно (clicker-kiln.js) кладе туди свій вид. Частина могла ще й не
  /// завантажитись — тоді кроки 3–4 просто бліді, а «Далі» веде по колу й сушарні.
  const kilnOf = (st) => st.kView || null;
  const burnMs = (st) => (st.catalog && st.catalog.kiln && st.catalog.kiln.burnMs) || 30000;
  /// Від якої краси розпису в партії трапляються розкішні (сервер: PaintLux у каталозі горна).
  const luxFrom = (st) => (st.catalog && st.catalog.kiln && st.catalog.kiln.paintLux) || 50;

  /// Скільки сухих сирців на сушарні просто зараз (горно рахує те саме, але може відставати на пів секунди).
  const dryNow = (st, api) => (st.craft ? st.craft.rack.filter((r) => r.dryAt <= api.serverNow(st)).length : 0);

  /// Скільки виробів піде в горно, якщо натиснути «Обпалити»: те, що вже складено, плюс сухі, що влізуть.
  function loadNow(st, api) {
    const k = kilnOf(st);
    if (!k) return 0;
    const free = Math.max(0, k.slots - k.batch.length);
    return k.batch.length + Math.min(dryNow(st, api), free);
  }

  /// Поки гончар не обпалив стільки партій-виробів, смуга про розпис мовчить: перше коло має бути коротким.
  const PAINT_FROM = 20;

  const selfFire = (api) => api.storeGet('clk.kiln.self', '0') === '1';
  const smoothOk = () => !(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);

  /// Горно, комора й замовлення живуть в одній вкладці «Ремесло» — кроки смуги ведуть саме туди.
  const CRAFT_TAB = 'craft';

  /// Розпалити горно одним дотиком: спершу скласти сухі (якщо є куди), потім запалити. Палить підмайстер, поки
  /// гравець сам не обрав «Палю сам» — тоді ведемо його до горна, де вже мінігра.
  /// o.bare — уже вирішено явно (кнопка «Без розпису», «Обпалити» з підсумку розпису): не питати.
  function fireKiln(st, api, o) {
    const k = kilnOf(st);
    if (!k) return;
    const ask = !(o && o.bare) && api.kilnAsk;
    // v12, плейтест §2.3: «Палю сам» без розпису мовчки пропускав розпис, а підмайстер мовчки палив зароблену красу
    // (98 → 0). Тепер обидва випадки — питання; «Далі» йде сюди ж.
    if (ask && selfFire(api) && !(k.beauty > 0) && api.kilnCanPaint && api.kilnCanPaint()) {
      ask({
        title: '🎨 Партія не розписана',
        text: 'Палиш сам — розписані вироби дзвінкіші, а з красою від ' + luxFrom(st) + ' бувають і розкішні (×4,5). Розписати перед обпалом?',
        btns: [{ label: '🎨 Розписати', run: () => api.startPaint(st) }, { label: '🔥 Обпалити без розпису', run: () => fireKiln(st, api, { bare: true }) }],
      });
      return;
    }
    if (ask && !selfFire(api) && k.beauty > 0) {
      ask({
        title: '👷 Підмайстер зітре красу',
        text: 'Партія розписана (краса ' + k.beauty + '), а підмайстер палить без краси розпису — вона згорить. Палити самому?',
        btns: [
          { label: '🔥 Палю сам', run: () => { api.storeSet('clk.kiln.self', '1'); fireKiln(st, api, { bare: true }); } },
          { label: '👷 Хай палить — без краси', run: () => fireKiln(st, api, { bare: true }) },
        ],
      });
      return;
    }
    const light = () => {
      if (selfFire(api)) {
        const useStraw = k.straw > 0 && api.storeGet('clk.kiln.straw', '1') === '1';
        // Мітка «розпалили звідси»: це вікно відкриє горно навіть без жодної дії, інші — почекають на нього (хотфікс B1).
        st.kLitPress = Date.now();
        quietAct(st, api, 'kiln', { op: 'light', straw: useStraw }).then((r) => {
          if (!r || !r.ok) st.kLitPress = 0;
          api.showTab(st, CRAFT_TAB);
        });
      } else {
        api.order(st, 'kiln', { op: 'light', helper: true });
      }
    };
    // Старт горна — без тостів «🧱 У горно…» і «🔥 Горно розпалено…» (QA-2: на телефоні два тости лягали на «Поліно» й
    // «Заслінку»): ручний обпал видно й так, а підмайстер скаже одним тостом про розпал.
    const free = Math.max(0, k.slots - k.batch.length);
    if (free > 0 && dryNow(st, api) > 0) quietAct(st, api, 'kiln', { op: 'load' }).then(light);
    else light();
  }

  /// Тиха дія (ctx.tell): без тосту про успіх, а відмову однаково кажемо тостом. Горну кліки, що ще не полетіли, не
  /// заважають — їх доносить наступна пачка.
  function quietAct(st, api, action, payload) {
    if (!st.ctx || !st.ctx.tell) return api.act(st, action, payload);
    return st.ctx.tell(action, payload).then((r) => {
      if (r && !r.ok && r.message) api.toast(st, r.message, 'err');
      return r || { ok: false, message: '' };
    });
  }

  /// Один наступний крок — те єдине, що гравцеві варто зробити зараз. Перше правило, яке підходить, і виграє;
  /// поки Око майстра питає — не радимо нічого (кола в ці секунди однаково не крутять).
  function nextStep(st, api) {
    const c = st.craft;
    if (!c || !st.mine || api.guardOn(st)) return null;
    const k = kilnOf(st);
    const sn = api.serverNow(st);
    const dry = dryNow(st, api);
    const load = loadNow(st, api);
    const word = (n) => api.plural(n, 'виріб', 'вироби', 'виробів');
    const rackFull = c.rack.length >= c.rackSize;

    // Готове замовлення села — завжди та сама порада, хоч би що робило горно. QA-3: «Далі» скакало між «Здати» й кораблем
    // чи Толокою 150 разів за хвилину, бо горно мінялось між «палає», «холоне» й «обпалити» — а з ним і порядок.
    const ready = ((st.fair && st.fair.orders) || []).filter((o) => o.until > sn && o.have >= o.n)[0];
    const deliver = ready && st.fairDeliver
      ? { key: 'deliver', icon: '📜', text: 'Здати замовлення · +' + api.short(ready.pay), sub: ready.whoText || '', btn: '🤝 Здати',
        run: (ev) => st.fairDeliver(ready.id, ev) }
      : null;
    // QA-2 v12: лідерові «нема сенсу клікати» — бо «Далі» вічно стояло на рутині горна з підмайстром і ніколи не
    // показувало ні корабля, ні відчиненої майстерні, ні Толоки. Стани горна, де від гравця нічого не треба (палить чи
    // відкриє підмайстер, горно холоне, «Обпалити» дві-три штуки чи те, що палій однаково візьме сам), поступаються
    // спершу готовому замовленню, тоді цілям частин (HClicker.goals: кораблі, майстерні, Толока, гості) — goalStep.
    const quiet = (s) => deliver || goalStep(st, api, 1) || s;

    // 1. Горно палає — чекаємо (або йдемо доглядати, якщо палимо самі).
    if (k && k.state === 'burning') {
      const left = Date.parse(k.litAt) + burnMs(st) - sn;
      return k.helper
        ? quiet({ icon: '🔥', text: 'Горно палає · ' + api.mmss(left), sub: 'підмайстер відкриє сам' })
        : { icon: '🔥', text: 'Горно палає — тримай жар', btn: 'До горна', run: () => api.showTab(st, CRAFT_TAB) };
    }
    // 2. Партія складена, а рука до неї ще не торкалась: спершу розпис (v9) — розписана партія дає дзвінкіші
    // й розкішні вироби. Стоїть перед «Обпалити» навмисно: після обпалу розписувати вже нічого.
    // Новачкові крок не показуємо — перше коло має бути коротким, тож лише після TIP_OLD обпалених.
    if (k && k.state === 'loaded' && k.batch.length > 0 && !k.beauty && (k.techs || []).length > 0 && c.fired >= PAINT_FROM) {
      return { icon: '🎨', text: 'Розписати партію · ' + k.batch.length + ' ' + word(k.batch.length),
        sub: 'розписані вироби дзвінкіші й дорожчі', btn: '🎨 Розписати',
        run: () => { api.showTab(st, CRAFT_TAB); api.startPaint && api.startPaint(st); } };
    }
    // 3. Є що обпалити — одна кнопка робить усе: складає сухі й розпалює. «Палю сам» без розпису (v12, плейтест §2.3:
    // «Далі» жодного разу не пропонував розписати) — fireKiln спершу спитає «розписати?», а «без розпису» — лише явно.
    let later = null;
    if (k && load > 0 && k.state !== 'cooling') {
      const unpainted = selfFire(api) && !(k.beauty > 0) && api.kilnCanPaint && api.kilnCanPaint();
      const fire = { icon: '🔥', text: 'Обпалити ' + load + ' ' + word(load),
        sub: rackFull ? 'сушарня повна — коло стоїть' : unpainted ? 'партія не розписана — спершу розпис?'
          : selfFire(api) ? 'палиш сам — буде мінігра' : 'палить підмайстер',
        btn: '🔥 Обпалити', run: () => fireKiln(st, api) };
      if (selfFire(api)) {
        // «Палю сам» (QA-3: у новачка «Обпалити 1…6» стояло 25+ хв і затуляло все): готове замовлення й віха, яку вже
        // можна купити, — вище; ручний обпал — від трьох виробів (чи коли сушарня повна), а менше — коли більше нічого.
        const up = deliver || markStep(st, api);
        if (up) return up;
        if (load >= 3 || rackFull) return fire;
        later = fire;
      } else {
        // Палить підмайстер, а сухих дві-три або палій однаково візьме сам — це рутина, не справа гравця.
        return load <= 2 || k.auto ? quiet(fire) : fire;
      }
    }
    // 4. Горно холоне — сухим доведеться зачекати.
    if (k && k.state === 'cooling' && dry > 0) {
      return quiet({ icon: '♨', text: 'Горно холоне · ' + api.mmss(Date.parse(k.coolUntil) - sn), sub: 'сухі чекають на нього' });
    }
    // 5. Замовлення вже можна здати.
    if (deliver) return deliver;
    // 5.5. Корабель, відчинена майстерня, Толока, гість — раніше за «Продати все»: корабель платить за ті самі вироби більше.
    const goal = goalStep(st, api, 1);
    if (goal) return goal;
    // 6. Сушарня повна, а сохне ще довго — підмайстри стоять, і це затор.
    if (rackFull) {
      const soon = c.rack.map((r) => r.dryAt).sort((a, b) => a - b)[0] || 0;
      return { icon: '🧺', text: 'Сушарня повна · ' + api.mmss(soon - sn), sub: 'поки не звільниться — коло стоїть' };
    }
    // 7. Комора набралась — час продати. Базар відкладеного (🔒 Толока, замовлення) не бере — тож і кнопка «Продати
    // вільне», а не «Продати все», як однойменна в коморі, що після «Точно?» продає й відкладене (QA-3).
    const items = c.items.reduce((s2, it) => s2 + it.n, 0);
    if (items > 0 && items >= c.storeCap / 2) {
      const kept = c.items.reduce((s2, it) => s2 + keptOf(it), 0);
      const free = items - kept;
      const sum = c.items.reduce((s2, it) => s2 + it.price * (it.n - keptOf(it)), 0);
      if (free > 0) {
        return { key: 'sell', icon: '📦', text: 'Продати ' + free + ' ' + word(free) + ' · +' + api.short(sum),
          sub: kept ? 'комора майже повна · 🔒 ' + kept + ' відкладених лишаться' : 'комора майже повна',
          btn: kept ? 'Продати вільне' : 'Продати все', arm: sellArm(kept), run: () => api.order(st, 'bazaar', { all: true }) };
      }
    }
    // 7.5. Перше коло гравець мусить пройти цілим: поки він не обпалив жодного виробу, сушарня важливіша
    // за будь-яку покупку — інакше він не побачить, звідки в коморі беруться вироби.
    if (c.fired === 0 && c.rack.length > 0 && !later) {
      const soon = c.rack.map((r) => r.dryAt).sort((a2, b2) => a2 - b2)[0] || 0;
      return { icon: '🧺', text: 'Сохне · ' + api.mmss(soon - sn), sub: 'висохне — і в горно' };
    }
    // 8. Найближча покупка — те, що раніше показував банер «Наступна ціль». Але поки гравець не виліпив
    // жодного виробу, порада «купи верстат» лише збиває: перше, що він мусить зробити, — крутнути коло.
    const g = c.formed > 0 || c.rack.length ? (api.goalOf ? api.goalOf(st) : null) : null;
    if (g) return goalToStep(st, api, g);
    // 8.5. «Палю сам» і одна-дві штуки — коли більше нічого робити.
    if (later) return later;
    // 9. Нічого термінового — просто ліпи.
    return { icon: '🏺', text: 'Крути коло — ліпиться ' + wareName(st, c.ware).toLowerCase(), sub: 'кожен клік — одна робота' };
  }

  /// Ціль (goalOf сцени чи HClicker.goals) — кроком «Далі»; short — підпис для другої кнопки («🚢 Корабель з Афін»).
  function goalToStep(st, api, g) {
    const emoji = (/>([^<]+)<\/text>/.exec(g.icon || '') || [])[1];
    const head = String(g.text || '').split(/[:·—]/)[0].trim();
    return { key: 'goal', icon: g.icon, svg: true, text: g.text, sub: g.sub || '', pct: g.pct,
      eta: g.eta > 0 && Number.isFinite(g.eta) ? '≈ ' + api.span(g.eta) : g.eta === 0 ? 'готово' : '',
      // Ціль може мати свою дію (B, QA-3: «🎨 Ліпити глечик → розпис»), інакше — «Глянути».
      btn: g.btn || (g.tab || g.row ? 'Глянути' : ''), run: g.run || (() => goToGoal(st, api, g)),
      short: (emoji ? emoji + ' ' : '') + (head.length <= 18 ? head : 'Глянути') };
  }

  /// Віха, яку вже можна купити (goalOf сцени: рядок віхи й повна смужка): у «Палю сам» вона вища за «Обпалити».
  function markStep(st, api) {
    const g = api.goalOf ? api.goalOf(st) : null;
    return g && /^\[data-mark=/.test(g.row || '') && g.pct >= 100 ? goalToStep(st, api, g) : null;
  }

  /// Найважливіша ціль частин (HClicker.goals: кораблі й майстерні — clicker-trade.js, Толока, гості) з пріоритетом не
  /// нижче maxPrio — у вигляді кроку «Далі». Цілі Майстерні (верстати, віхи) сюди не йдуть: вони — у goalOf, крок 8.
  function goalStep(st, api, maxPrio) {
    const v = st.lastView;
    if (!v || !st.mine) return null;
    let best = null;
    for (const fn of window.HClicker.goals || []) {
      let g = null;
      try { g = fn(st, v, api); } catch (e) { console.error('[clicker:craft] ціль', e); }
      const pa = g ? g.prio ?? 5 : Infinity;
      if (pa > maxPrio) continue;
      const pb = best ? best.prio ?? 5 : Infinity;
      if (pa < pb || (pa === pb && (g.eta ?? Infinity) < (best.eta ?? Infinity))) best = g;
    }
    return best ? goalToStep(st, api, best) : null;
  }

  /// Друга кнопка рядка «Далі» — на щохвилинні випадки, до яких на телефоні від кола 900+ px прокрутки: готове
  /// замовлення, ціль частин (корабель, Толока — коли перша кнопка «Здати», QA-3) і повна комора. Решта порад лишається
  /// одна — смуга не має перетворитись на другу панель. Дії ті самі, що й кнопки на ярмарку й у коморі.
  function secondStep(st, api, n) {
    const c = st.craft;
    if (!c || !n || !st.mine) return null;
    const sn = api.serverNow(st);
    const ready = st.fairDeliver && ((st.fair && st.fair.orders) || []).filter((o) => o.until > sn && o.have >= o.n)[0];
    if (ready && n.key !== 'deliver') {
      return { key: 'deliver', btn: '🤝 Здати', title: 'Здати замовлення · +' + api.short(ready.pay),
        run: (ev) => st.fairDeliver(ready.id, ev) };
    }
    if (n.key !== 'goal') {
      const g = goalStep(st, api, 1);
      if (g) return { key: 'goal', btn: g.short, title: g.text, run: g.run };
    }
    const items = c.items.reduce((s2, it) => s2 + it.n, 0);
    if (items > 0 && items >= c.storeCap && n.key !== 'sell') {
      const kept = c.items.reduce((s2, it) => s2 + keptOf(it), 0);
      const sum = c.items.reduce((s2, it) => s2 + it.price * (it.n - keptOf(it)), 0);
      if (items - kept > 0) {
        return { key: 'sell', btn: kept ? '📦 Продати вільне' : '📦 Продати все', arm: sellArm(kept),
          title: 'Комора повна: продати ' + (items - kept) + ' ' + api.plural(items - kept, 'виріб', 'вироби', 'виробів') + ' · +' + api.short(sum),
          run: () => api.order(st, 'bazaar', { all: true }) };
      }
    }
    return null;
  }

  function goToGoal(st, api, g) {
    if (g.tab && st.panes[g.tab]) api.showTab(st, g.tab);
    // Ціль просить режим покупки (перший рівень заморської майстерні — ×1, як ★): той самий перемикач, що в рядку режимів.
    const mb = g.mode && st.modes && st.modes.querySelector('[data-mode="' + g.mode + '"]');
    if (mb && !mb.classList.contains('active')) mb.click();
    if (!g.row) return;
    // Рядок цілі частина домальовує, щойно її вкладка на очах (кораблі, Толока в «Селі»), — шукаємо ще кілька кадрів,
    // а не здаємось одразу (QA-2: «Глянути» на кораблі перемикало вкладку, а до корабля не вело).
    const row = st.el.querySelector(g.row);
    if (!row) {
      const tries = (g.tries || 0) + 1;
      if (tries < 12) setTimeout(() => { if (st.el) goToGoal(st, api, Object.assign({}, g, { tab: null, tries })); }, 100);
      return;
    }
    // v12 §2.6: посередині видимого, над нижнім меню, і з підсвіткою (clicker.js, lookAt).
    if (api.lookAt) { api.lookAt(st, row); return; }
    row.scrollIntoView({ block: 'nearest', behavior: smoothOk() ? 'smooth' : 'auto' });
    row.classList.remove('flash');
    void row.offsetWidth;
    row.classList.add('flash');
  }

  /// Чотири кроки шляху: активний той, де зараз є що робити; блідий — той, до якого ще не дійшли.
  function pathSteps(st, api) {
    const c = st.craft;
    const k = kilnOf(st);
    const sn = api.serverNow(st);
    const dry = dryNow(st, api);
    const items = c.items.reduce((s, it) => s + it.n, 0);
    const sum = c.items.reduce((s, it) => s + it.price * it.n, 0);
    const work = Math.floor(workNow(st) % Math.max(1, c.need));
    // Плитка — значення (v) і дрібний хвіст під ним (x), без назви (UI-прохід 21.10, друга хвиля): що це за крок, каже
    // значок, а назва — у підказці. Назви з'їдали півплитки й різали головне — «46/46 · сухих …», «592 · ~2,19 м…».
    const kiln = !k ? { v: '—', x: '' }
      : k.state === 'burning' ? { v: api.mmss(Date.parse(k.litAt) + burnMs(st) - sn), x: 'палає' }
      : k.state === 'cooling' ? { v: '♨ холоне', x: '' }
      : k.batch.length ? { v: 'складено ' + k.batch.length, x: '' }
      // Сухі є — горно не «чекає сухих» (QA-3: так писало при «сухих 6»), а чекає, щоб його розпалили.
      : dry ? { v: 'палити!', x: 'сухих ' + dry } : { v: 'холодне', x: '' };
    // Сушарня повна — коло стоїть (підмайстрам нікуди класти), і гра мусить це сказати.
    const stuck = c.rack.length >= c.rackSize;
    return [
      { key: 'wheel', ico: api.wareSvg(c.ware, { cls: 'clkw-sico', slot: 'step-ware', clay: st.clayBody }),
        name: wareName(st, c.ware), v: stuck ? 'стоїть' : work + '/' + c.need + ' ▾', x: stuck ? 'сушарня повна' : wareName(st, c.ware),
        pct: (work / Math.max(1, c.need)) * 100,
        on: true, hot: stuck },
      // ⓘ на трьох чипах веде до секції «🔧 Прокачати»: місткість — не магія, її видно й видно, звідки вона.
      { key: 'rack', ico: '🧺', name: 'Сушарня', v: c.rack.length + '/' + c.rackSize, x: dry ? 'сухих ' + dry : c.rack.length ? 'сохне' : 'порожня',
        pct: (c.rack.length / Math.max(1, c.rackSize)) * 100, on: c.rack.length > 0, hot: dry > 0, ups: true },
      // Сухі є, а горно холодне — це і є наступний крок (UI-прохід 21.10: плитка була блідою, як «ще не дійшли», хоч
      // «Далі» кликало обпалити й підпис казав «можна палити»).
      { key: 'kiln', ico: '🔥', name: 'Горно', v: kiln.v, x: kiln.x, on: !!k && (k.batch.length > 0 || k.state !== 'cold' || dry > 0),
        hot: !!k && (k.state === 'burning' || (k.state === 'cold' && dry > 0)), ups: true },
      { key: 'store', ico: '📦', name: 'Комора', v: items ? api.count(items) : 'порожня', x: items ? '≈ ' + api.short(sum) : '',
        pct: (items / Math.max(1, c.storeCap)) * 100, on: items > 0, ups: true },
    ];
  }

  function paintPath(st, api) {
    const c = st.craft;
    const ui = st.craftUi;
    if (!c || !ui) return;
    const esc = (x) => api.esc(st, x);
    const steps = pathSteps(st, api);
    // Чип — кнопка, а ⓘ поруч — окрема кнопка (кнопка в кнопці недійсна, і з клавіатури до неї не дістатись):
    // обидві в обгортці .clk-stepw, яка й стоїть у сітці кроків.
    // Каркас (кнопки, значки, назви, класи) міняється рідко — його й порівнює swap. Рядок під назвою («17/40»,
    // «складено 6») і смужки живуть на місці: раніше вся смуга перемальовувалась innerHTML-ом на кожен клік, а з нею
    // SVG виробу й розкладка картки (десяте оновлення, §10).
    const html = steps.map((s) => '<span class="clk-stepw' + (s.ups ? ' hasi' : '') + '"><button type="button" class="clk-step' + (s.on ? ' on' : '') + (s.hot ? ' hot' : '')
      + (s.ups ? ' hasi' : '') + '" data-step="' + s.key + '">'
      + '<span class="clk-stico">' + (s.ico.charAt(0) === '<' ? s.ico : esc(s.ico)) + '</span>'
      + '<span class="clk-sttxt"><span class="clk-stv"></span><span class="clk-stx"></span></span>'
      + (s.pct != null ? '<i class="clk-stbar"><i></i></i>' : '')
      + '</button>'
      + (s.ups ? '<button type="button" class="clk-sti" data-ups="1" aria-label="Звідки ця місткість і як її збільшити" title="Звідки ця місткість і як її збільшити">ⓘ</button>' : '')
      + '</span>').join('');
    if (api.swap(ui.steps, html)) {
      for (const b of ui.steps.querySelectorAll('[data-step]')) b.onclick = (ev) => stepClick(st, api, b.dataset.step, ev);
      for (const i of ui.steps.querySelectorAll('.clk-sti')) {
        i.onclick = () => { api.sfx('tap'); showUps(st, api, i.previousElementSibling && i.previousElementSibling.dataset.step); };
      }
      ui.stepEls = {};
      for (const b of ui.steps.querySelectorAll('[data-step]')) {
        ui.stepEls[b.dataset.step] = { v: b.querySelector('.clk-stv'), x: b.querySelector('.clk-stx'), btn: b, bar: b.querySelector('.clk-stbar i') };
      }
      ui.wheelBar = ui.stepEls.wheel ? ui.stepEls.wheel.bar : null;
    }
    for (const s of steps) {
      const el = ui.stepEls && ui.stepEls[s.key];
      if (!el) continue;
      if (el.v.textContent !== s.v) el.v.textContent = s.v;
      if (el.x.textContent !== s.x) el.x.textContent = s.x;
      const title = s.name + ': ' + s.v + (s.x ? ' · ' + s.x : '');
      if (el.btn.title !== title) el.btn.title = title;
      if (s.pct != null) setBar(el.bar, s.pct / 100);
    }
    paintNext(st, api);
  }

  function stepClick(st, api, step, ev) {
    api.sfx('tap');
    // ⓘ на чипі — не «піди до сушарні», а «звідки ця місткість»: веде до секції прокачки.
    if (ev && ev.target && ev.target.closest && ev.target.closest('[data-ups]')) { showUps(st, api, step); return; }
    if (step === 'wheel') { openPicker(st, api); return; }
    api.showTab(st, CRAFT_TAB);
    const sel = { rack: '.clkw-rack-sec, .clkk', kiln: '.clkk', store: '.clkw-items, .clkw-store' }[step];
    const el = sel && st.el.querySelector(sel);
    if (el) el.scrollIntoView({ block: 'nearest', behavior: smoothOk() ? 'smooth' : 'auto' });
  }

  /// Показати секцію «🔧 Прокачати» й підсвітити її: саме туди ведуть ⓘ зі смуги. Новачкові вкладки «Ремесло»
  /// ще нема — тоді ⓘ просто розказує те саме словами, а не кидає його казна-куди.
  /// QA-2: ⓘ обіцяє пояснення, а мовчки перекидало в «Прокачати». Тепер — спливайка про саме цю місткість і кнопка
  /// «До прокачки» (якщо вкладка «Ремесло» вже є).
  function showUps(st, api, step) {
    const tab = st.tabs && st.tabs.querySelector('[data-tab="' + CRAFT_TAB + '"]');
    const el = st.upsBody;
    const can = !!el && !!tab && !tab.hidden;
    const one = UPS_STEP[step];
    const body = api.overlay(st, '<div class="clk-sub">🔧 ' + (one ? api.esc(st, one[0]) : 'Звідки береться місткість') + '</div>'
      + '<p class="small">' + api.esc(st, one ? one[1] + ' ' + UPS_TAIL : UPS_INFO) + '</p>'
      + (can ? '<button type="button" class="primary clkw-toups">🔧 До прокачки</button>' : ''));
    const go = body.querySelector('.clkw-toups');
    if (go) go.onclick = () => { api.closeOverlay(st); toUps(st, api, step); };
  }

  /// Ключ рядка прокачки для кроку смуги (ClickerCraft.CraftUps).
  const UP_OF = { rack: 'rack', kiln: 'kilnroom', store: 'store' };

  /// До «🔧 Прокачати»: розгорнути згортку й підсвітити рядок саме цієї місткості (QA-3: лише докручувало до
  /// згорнутої секції).
  function toUps(st, api, step) {
    const el = st.upsBody;
    if (!el) return;
    api.showTab(st, CRAFT_TAB);
    if (api.unfold) api.unfold(st, el);
    // Рядок шукаємо вже після розгортання: згортка перемальовує секцію, і вона ще росте, поки рядки оживають (QA-4:
    // зі згорнутої секції «Горно» лишалось під краєм, y 762 при видимих 701). Тож дивимось двічі — одразу й коли
    // розкладка вляглась, і вдруге гортаємо, лише якщо рядок і досі не на екрані.
    const show = (again) => {
      if (!st.upsBody) return;
      const row = (UP_OF[step] && st.upsBody.querySelector('[data-upkey="' + UP_OF[step] + '"]')) || st.upsBody;
      if (again) {
        const r = row.getBoundingClientRect();
        if (r.top >= 60 && r.bottom <= window.innerHeight - 70) return;
      }
      if (api.lookAt) { api.lookAt(st, row); return; }
      row.scrollIntoView({ block: 'center', behavior: smoothOk() ? 'smooth' : 'auto' });
      row.classList.remove('flash');
      void row.offsetWidth;
      row.classList.add('flash');
    };
    requestAnimationFrame(() => show(false));
    setTimeout(() => show(true), 700);
  }

  /// Напис кнопки «Далі»: значок окремо від слів («🚢» + « Корабель з Афін») — на телефоні друга кнопка лишає лише
  /// значок (css .clk-nxl), щоб порада й обидві кнопки стали в один рядок. textContent однаковий в обох видах.
  function nxLabel(b, label, esc) {
    if (b.textContent === label) return;
    const m = /^(\S+)\s+(.+)$/.exec(label);
    b.innerHTML = m && !/[0-9A-Za-zА-ЯҐЄІЇа-яґєії]/.test(m[1])
      ? '<span class="clk-nxe">' + esc(m[1]) + '</span><span class="clk-nxl"> ' + esc(m[2]) + '</span>' : esc(label);
  }

  /// Рядок «Далі» — лише новачкові (23.10: «блок «Далі» можна взагалі прибрати»): поки обпалив менше TIP_OLD, він єдиний
  /// путівник і місце для підказок; далі гравець і сам знає дорогу, а під сценою стоять плашки бафів.
  function paintNext(st, api) {
    const ui = st.craftUi;
    const rookie = !!(st.craft && st.craft.fired < TIP_OLD);
    // Не новачкові — без рядка й без місця під нього (.off); новачкові порожній рядок місце тримає, щоб сцена не стрибала.
    if (ui.nx.classList.contains('off') === rookie) ui.nx.classList.toggle('off', !rookie);
    if (st.el && st.el.classList.contains('rookie') !== rookie) st.el.classList.toggle('rookie', rookie);
    const n = rookie ? nextStep(st, api) : null;
    ui.next = n;
    const show = !!n;
    if (ui.nx.hidden === show) ui.nx.hidden = !show;
    if (!n) return;
    const esc = (x) => api.esc(st, x);
    const ico = n.svg ? '<svg class="clk-nxsvg" viewBox="0 0 32 32" aria-hidden="true">' + n.icon + '</svg>' : esc(n.icon);
    api.swap(ui.nxIco, ico);
    const tip = ui.tip && Date.now() < ui.tip.until ? ui.tip.text : '';
    const text = tip ? '💡 ' + tip : 'Далі: ' + n.text;
    if (ui.nxText.textContent !== text) ui.nxText.textContent = text;
    const sub = tip ? '' : n.sub || n.eta || '';
    // На екрані порада й підрядок — разом не більше двох рядків (QA-3), повністю — у підказці.
    const full = sub ? text + ' · ' + sub : text;
    if (ui.nxTxt.title !== full) ui.nxTxt.title = full;
    if (ui.nx.classList.contains('tip') !== !!tip) ui.nx.classList.toggle('tip', !!tip);
    if (ui.nxSub.textContent !== sub) ui.nxSub.textContent = sub;
    if (ui.nxSub.hidden !== !sub) ui.nxSub.hidden = !sub;
    const label = n.btn || '';
    // Кнопку під пальцем не перемальовуємо, поки її «звели» другим натиском.
    if (ui.armed && Date.now() > ui.armed) ui.armed = 0;
    if (!ui.armed) nxLabel(ui.nxBtn, label, esc);
    if (ui.nxBtn.hidden !== !label) ui.nxBtn.hidden = !label;
    const off = !label || !st.mine || !n.run;
    if (ui.nxBtn.disabled !== off) ui.nxBtn.disabled = off;
    if (ui.nxBtn.classList.contains('armed') !== !!ui.armed) ui.nxBtn.classList.toggle('armed', !!ui.armed);
    setBar(ui.nxBar, n.pct != null ? n.pct / 100 : 0);
    const m = secondStep(st, api, n);
    ui.next2 = m;
    const b2 = ui.nxBtn2;
    if (ui.armed2 && Date.now() > ui.armed2) ui.armed2 = 0;
    const label2 = m ? m.btn : '';
    if (!ui.armed2) nxLabel(b2, label2, esc);
    if (b2.hidden !== !m) b2.hidden = !m;
    // На телефоні друга кнопка — лише значок: без підказки її не прочитати (і читачам екрана теж).
    const title2 = m ? m.title || m.btn : '';
    if (b2.title !== title2) b2.title = title2;
    if (b2.classList.contains('armed') !== !!ui.armed2) b2.classList.toggle('armed', !!ui.armed2);
    if (ui.nx.classList.contains('two') !== !!m) ui.nx.classList.toggle('two', !!m);
  }

  // ---------- підказки «перший раз» ----------

  const TIP_MS = 8000;                    // стільки висить підказка, якщо гравець її не закрив
  const TIP_OLD = 20;                     // обпалив стільки — уже не новачок, підказок не показуємо

  /// П'ять підказок на всю гру, по одній, кожна раз у житті (clk.tip.<key> у localStorage). Вони пояснюють
  /// саме те, що гравець бачить просто зараз, — і зникають самі.
  const TIPS = [
    { key: 'spin', text: 'Кожен клік ліпить горщик — дивись смужку на першому кроці',
      when: (c) => c.formed === 0 && c.work > 0 },
    { key: 'rack', text: 'Виріб сохне на сушарні півтори хвилини, а потім піде в горно',
      when: (c) => c.rack.length > 0 },
    { key: 'dry', text: 'Сирець висох — тисни «Обпалити», решту горно зробить саме',
      when: (c, st, api) => dryNow(st, api) > 0 },
    { key: 'fired', text: 'Виріб у коморі: продай його або притримай для замовлення',
      when: (c) => c.items.length > 0 },
    { key: 'craft', text: 'Усе ремесло тепер на вкладці «Ремесло» — горно, комора й замовлення',
      when: (c, st) => c.fired > 0 && !!st.panes.craft },
  ];

  function maybeTip(st, api) {
    const c = st.craft;
    const ui = st.craftUi;
    if (!c || !ui || !st.mine || c.fired >= TIP_OLD || (ui.tip && Date.now() < ui.tip.until)) return;
    for (const t of TIPS) {
      if (api.storeGet('clk.tip.' + t.key, '') === '1') continue;
      let on = false;
      try { on = !!t.when(c, st, api); } catch (e) { console.error('[clicker:craft] підказка ' + t.key, e); }
      if (!on) continue;
      api.storeSet('clk.tip.' + t.key, '1');
      showTip(st, api, t.text);
      return;
    }
  }

  /// Підказка — у самому рядку «Далі» замість поради на TIP_MS (QA-3): окремою плашкою вона лягала на ряд вкладок
  /// (на 1366 — y 182–213 поверх 186–238), а з reduced-motion висіла, доки не тапнеш, і наступні чекали на неї по 25 хв.
  /// Тут — ні накладань, ні стрибків висоти; кнопки «Далі» лишаються живі, тап по тексту — прибрати.
  function showTip(st, api, text) {
    const ui = st.craftUi;
    ui.tip = { text, until: Date.now() + TIP_MS };
    clearTimeout(ui.tipT);
    ui.tipT = setTimeout(() => { if (st.craftUi === ui) { ui.tip = null; paintNext(st, api); } }, TIP_MS + 50);
    paintNext(st, api);
  }

  function runNext(st, api, ev, two) {
    const ui = st.craftUi;
    const n = two ? ui.next2 : ui.next;
    const btn = two ? ui.nxBtn2 : ui.nxBtn;
    const armKey = two ? 'armed2' : 'armed';
    if (!n || !n.run || !st.mine) return;
    // Незворотне (продати все) питаємо двічі — як і кнопка в коморі.
    // QA-2: 3 с замало, щоб прочитати й натиснути вдруге, — 4,5 с; і кнопка не звужується під коротший текст.
    if (n.arm && (!ui[armKey] || Date.now() > ui[armKey])) {
      ui[armKey] = Date.now() + ARM_MS;
      btn.style.minWidth = btn.offsetWidth + 'px';
      btn.textContent = n.arm;
      btn.classList.add('armed');
      setTimeout(() => {
        if (st.craftUi !== ui || Date.now() < ui[armKey]) return;
        ui[armKey] = 0;
        btn.style.minWidth = '';
        paintNext(st, api);
      }, ARM_MS + 50);
      return;
    }
    ui[armKey] = 0;
    btn.style.minWidth = '';
    btn.classList.remove('armed');
    api.sfx('tap');
    n.run(ev);
  }

  // ---------- вибір виробу ----------

  /// Скільки ще виробів кожного виду просить Толока: поточний етап (що не піднесли друзі, нема на складі й у коморі), а коли
  /// він уже будується — наступний. Бейдж «🧱 Толока: ще 58» у виборі виробу (v12, плейтест §2.3).
  function tolokaWant(st) {
    const t = st.lastView && st.lastView.toloka;
    const out = {};
    if (!t) return out;
    const s = t.stage;
    const list = s && !s.laidAt ? s.needs.map((n) => ({ ware: n.ware, left: n.n - (n.got || 0) - (n.depot || 0) - (n.have || 0) }))
      : t.next && t.next.needs ? t.next.needs.map((n) => ({ ware: n.ware, left: n.n - (n.have || 0) })) : [];
    for (const x of list) if (x.left > 0) out[x.ware] = (out[x.ware] || 0) + x.left;
    return out;
  }

  function openPicker(st, api) {
    const c = st.craft;
    if (!c || !st.mine) return;
    const want = tolokaWant(st);
    const cards = c.wares.map((w) => {
      const on = w.key === c.ware;
      const cat = st.catalog && st.catalog.wares && st.catalog.wares.find((x) => x.key === w.key);
      const sec = cat ? cat.seconds : 0;
      return '<button type="button" class="clkw-card' + (on ? ' on' : '') + (w.open ? '' : ' locked') + '" data-ware="' + api.esc(st, w.key) + '"'
        + (w.open && !on ? '' : ' disabled') + '>'
        + api.wareSvg(w.key, { cls: 'clkw-big', slot: 'pick-' + w.key, clay: st.clayBody, quality: 1 })
        + '<b>' + api.esc(st, w.name) + '</b>'
        // Робота й ціна — окремими рядками: «робота 16 · ~140 987 золотих» одним рядком вилазило за картку (B13).
        + (w.open && want[w.key] ? '<span class="clkw-tol small">🧱 Толока: ще ' + api.count(want[w.key]) + '</span>' : '')
        + (w.open
          ? '<span class="muted small">робота ' + w.need + '</span>' + (sec ? '<span class="muted small">~' + api.potsShort(w.value * (c.bazaar || 1)) + '</span>' : '')
            + '<span class="muted small">обпалено ' + api.num(w.fired) + '</span>'
            + (on ? '<span class="clk-price done">на колі</span>' : '<span class="clk-price">ліпити</span>')
          : '<span class="muted small">відкриється на ' + api.potsShort(w.unlock) + ' за весь час</span>')
        + '</button>';
    }).join('');
    const body = api.overlay(st, '<div class="clk-sub">Що ліпити на колі'
      + api.info('Кожен зарахований клік — одна робота; підмайстри ліплять і без тебе. Готовий виріб сохне на сушарні, а '
        + 'висохлий обпалюють у горні — тоді він з розписом і якістю ляже в комору. Дорожчий виріб довше ліпити, зате він '
        + 'вартий більше. Ціна — простого звичайного, розпис і якість її множать.') + '</div>'
      + '<div class="clkw-grid">' + cards + '</div>', { cls: 'clkw-picker' });
    for (const b of body.querySelectorAll('[data-ware]')) {
      b.onclick = () => { api.order(st, 'form', { ware: b.dataset.ware }); api.closeOverlay(st); };
    }
  }

  // ---------- продаж гуртом ----------

  /// Скільки виробів і глеків забере базар, якщо брати якість до q включно (1 — звичайні, 2 — і добрі, 3 — усе).
  function sellUpTo(items, q) {
    let n = 0, sum = 0, kept = 0;
    for (const it of items) if (it.q <= q) { n += it.n; sum += it.price * it.n; kept += keptOf(it); }
    return { q, n, sum, kept, why: keptSum(items, q) };
  }

  /// Кнопки продажу: «все» завжди, а вужчі — лише коли вони справді щось лишають у коморі. Ніяких перемикачів і
  /// пам'яті: кожна кнопка сама каже, що забере й скільки за це дадуть.
  function sellButtons(items) {
    const all = sellUpTo(items, 4);
    if (!all.n) return [];
    const out = [Object.assign(all, { label: 'Продати все' })];
    const one = sellUpTo(items, 1);
    const two = sellUpTo(items, 2);
    const three = sellUpTo(items, 3);
    if (one.n > 0 && one.n < all.n) out.push(Object.assign(one, { label: 'Лише ★ звичайні' }));
    if (two.n > one.n && two.n < all.n) out.push(Object.assign(two, { label: 'Усе, крім ★★★ дзвінких' }));
    if (three.n > two.n && three.n < all.n) out.push(Object.assign(three, { label: 'Усе, крім 👑 розкішних' }));
    return out;
  }

  // ---------- комора ----------

  function paintStore(st, api) {
    const c = st.craft;
    if (!c || !st.storeBody || !st.rackBody) return;
    const esc = (x) => api.esc(st, x);
    const total = c.items.reduce((s, it) => s + it.n, 0);
    const sum = c.items.reduce((s, it) => s + it.price * it.n, 0);
    const now = api.serverNow(st);

    // Сушарня — між горном і коморою: видно, що вже сохне й скільки лишилось. На телефоні сорок шість однакових
    // значків «сухий» займали ~260 px між горном і коморою, тож це згортка: смуга «34 з 46» і кілька значків, а сітка —
    // за ▸. На ПК згортка відкрита, поки гравець сам її не закриє (st.rackOpen, до кінця монтування).
    // Сухі в сітці — одна плитка «×N» на виріб (і глину, і тонкий), далі мокрі з відліком (записка #66: дев'ятнадцять
    // однакових «сухий» на два рядки ховали ті чотири, що ще сохнуть).
    const dryN = c.rack.filter((r) => r.dryAt <= now).length;
    const dryGroups = [];
    for (const r of c.rack) {
      if (r.dryAt > now) continue;
      const g = dryGroups.find((x) => x.ware === r.ware && x.clay === r.clay && x.thin === !!r.thin);
      if (g) g.n++;
      else dryGroups.push({ ware: r.ware, clay: r.clay, thin: !!r.thin, n: 1 });
    }
    const peek = c.rack.slice(0, 6).map((r, i2) => wareSvg(api, r.ware, { raw: true, dry: r.dryAt <= now, clay: clayBody(st, r.clay),
      slot: 'speek-' + i2, cls: 'clkw-mini' })).join('');
    const rack = c.rack.length
      ? '<details class="clkw-rack-sec" data-key="rack"><summary><b>🧺 Сушарня · ' + c.rack.length + ' з ' + c.rackSize + '</b>'
        + (dryN ? '<span class="clkw-rackdry small">сухих ' + dryN + '</span>' : '')
        + '<span class="clkw-peek" aria-hidden="true">' + peek + (c.rack.length > 6 ? '<i>…</i>' : '') + '</span></summary>'
        + '<div class="clkw-rackrow">' + dryGroups.map((g, i2) => '<span class="clkw-rackitem dry' + (g.thin ? ' thin' : '') + '" title="'
            + esc(g.n + ' ' + api.plural(g.n, 'сухий', 'сухих', 'сухих') + ': ' + wareName(st, g.ware)
              + (g.thin ? ' — тонкі з каоліну, з горна вийдуть розкішними' : '') + ' — чекають горна') + '">'
            + wareSvg(api, g.ware, { raw: true, dry: true, clay: clayBody(st, g.clay), slot: 'strack-d' + i2, cls: 'clkw-mini' })
            + '<span class="small">×' + g.n + '</span></span>').join('')
        + c.rack.filter((r) => r.dryAt > now).map((r, i2) => '<span class="clkw-rackitem' + (r.thin ? ' thin" title="тонкий з каоліну — з горна вийде розкішним' : '') + '">'
            + wareSvg(api, r.ware, { raw: true, dry: false, clay: clayBody(st, r.clay), slot: 'strack-' + i2, cls: 'clkw-mini' })
            + '<span class="small"><span class="clk-cd" data-at="' + r.dryAt + '" data-done="сухий"></span></span></span>').join('')
        + '</div></details>'
      : '';

    // Комора: рядок на виріб, а розписи й якості — за ▾. Так чотирнадцять глеків читаються одним поглядом.
    const byWare = new Map();
    for (const it of c.items) {
      const g = byWare.get(it.ware) || { ware: it.ware, n: 0, sum: 0, best: 0, kept: 0, rows: [] };
      g.n += it.n;
      g.kept += keptOf(it);
      g.sum += it.price * it.n;
      g.best = Math.max(g.best, it.q);
      g.rows.push(it);
      byWare.set(it.ware, g);
    }
    const groups = [...byWare.values()].sort((a, b) => b.sum - a.sum);
    // Продаж гуртом: «Продати все» і, коли є що лишити, — вужчі кнопки. Кожна написана тими самими словами, що й
    // рядки комори («★ звичайний», «★★★ дзвінкий»), і одразу каже, скільки виробів забере й за скільки.
    const sells = sellButtons(c.items);
    const keptAll = keptSum(c.items, 4);
    const keptN = c.items.reduce((s, it) => s + keptOf(it), 0);
    // Комора — згортка (13.10); сушарня тепер окремо над замовленнями (v12), тож видна й тоді, коли комору згорнули.
    const head = api.foldHead('📦 Комора · ' + total + ' ' + api.plural(total, 'виріб', 'вироби', 'виробів')
      + (total ? ' · разом ~' + api.short(sum) : ''))
      + (total
        ? '<div class="clkw-sells">' + sells.map((b, i) => '<button type="button" data-sq="' + b.q + '"'
          + ' class="' + (i ? 'ghost small' : 'primary') + ' clkw-sellall"' + (st.mine ? '' : ' disabled') + '>'
          + esc(b.label) + ' · ' + b.n + ' ' + api.plural(b.n, 'виріб', 'вироби', 'виробів') + ' · +' + api.short(b.sum)
          + '</button>').join('') + '</div>'
          // Відкладене (v12, плейтест §2.4): видно, скільки чекають Толока й замовлення, і що продаж гуртом забере й їх.
          + (keptN ? '<div class="small clkw-keptline">🔒 Відкладено ' + keptN + ': ' + esc(keptText(keptAll))
            + ' — продаж гуртом забере й їх</div>' : '')
          + '<div class="muted small">Що не влізе в ' + c.storeCap + ' — продається саме.</div>'
        : '');
    const items = total
      ? '<div class="clkw-groups">' + groups.map((g) => {
        const rows = g.rows.slice().sort((a, b) => b.q - a.q || b.value - a.value).map((it) => '<div class="clkw-chiprow q' + it.q + '">'
          + '<span class="clkw-ctxt">' + esc(styleName(st, it.style)) + ' · <span class="clkw-q">' + STARS[it.q] + ' ' + QUALITY[it.q] + '</span>'
          + ' <span class="clkw-n">×' + it.n + '</span> <span class="muted">по ' + api.potsShort(it.price) + '</span>'
          + (keptOf(it) ? ' <span class="clkw-kept" title="Відкладено: цього чекають Толока, замовлення чи майстерштук">🔒 '
            + esc(keptText(it.kept)) + '</span>' : '') + '</span>'
          + '<span class="clkw-btns"><button type="button" class="ghost small" data-sell="' + esc(it.key) + '" data-n="1"' + (st.mine ? '' : ' disabled') + '>Продати</button>'
          + (it.n > 1 ? '<button type="button" class="ghost small" data-sell="' + esc(it.key) + '" data-n="' + it.n + '"' + (st.mine ? '' : ' disabled') + '>Усі ' + it.n + '</button>' : '')
          + '</span></div>').join('');
        return '<details class="clkw-group' + (g.best >= 3 ? ' star' : '') + '" data-key="' + esc(g.ware) + '"><summary>'
          + api.wareSvg(g.ware, { style: g.rows[0].style, quality: g.best, cls: 'clkw-mid', slot: 'st-' + g.ware })
          + '<span class="clkw-gtxt"><b>' + esc(wareName(st, g.ware)) + (g.best >= 3 ? ' <span class="clkw-q">' + STARS[g.best] + '</span>' : '') + '</b>'
          + '<span class="muted small">×' + g.n + ' · ~' + api.short(g.sum) + (g.kept ? ' · 🔒 ' + g.kept : '') + '</span></span></summary>' + rows + '</details>';
      }).join('') + '</div>'
      : '<div class="clk-teaser muted small">Комора порожня — сюди лягають вироби з горна.</div>';

    if (api.swap(st.rackBody, rack)) {
      const fold = st.rackBody.querySelector('.clkw-rack-sec');
      if (fold) {
        const narrow = window.matchMedia && window.matchMedia('(max-width: 480px), (pointer: coarse)').matches;
        fold.open = st.rackOpen != null ? st.rackOpen : !narrow;
        fold.ontoggle = () => { st.rackOpen = fold.open; };
      }
      st.storeCds = [...st.rackBody.querySelectorAll('.clk-cd')];
    }
    if (api.swap(st.storeBody, head + items)) {
      const line = st.storeBody.querySelector('.clkw-keptline');
      for (const b of st.storeBody.querySelectorAll('[data-sq]')) {
        const q = +b.dataset.sq;
        const sb = sells.find((x) => x.q === q) || { kept: 0, why: [0, 0, 0] };
        let armed = 0;
        b.onclick = () => {
          // «Продати все» питаємо двічі: дзвінкі й розписні теж поїдуть. Вужчі кнопки дороге лишають — там натиск один,
          // якщо не зачіпають відкладеного (v12): тоді перед продажем кажемо, що підуть і вироби для Толоки й замовлень.
          if ((q === 4 || sb.kept > 0) && (!armed || Date.now() > armed)) {
            armed = Date.now() + ARM_MS;
            const was = b.textContent;
            // Кнопка не звужується під коротший текст (QA-2: 369 → 234 px).
            b.style.minWidth = b.offsetWidth + 'px';
            b.textContent = sb.kept > 0 ? 'Точно? І 🔒 ' + sb.kept + ' відкладених' : sellArm(0);
            const lineWas = line ? line.textContent : '';
            if (line && sb.kept) { line.textContent = '🔒 Підуть і відкладені ' + sb.kept + ': ' + keptText(sb.why) + ' — їх доведеться ліпити знову'; line.classList.add('armed'); }
            setTimeout(() => {
              if (Date.now() < armed || !b.isConnected) return;
              b.textContent = was;
              b.style.minWidth = '';
              if (line) { line.textContent = lineWas; line.classList.remove('armed'); }
            }, ARM_MS + 50);
            return;
          }
          // Тут гравець бачив «Підуть і відкладені» й натиснув удруге — продаємо й їх (сервер без keep: false лишив би).
          api.order(st, 'bazaar', sb.kept > 0 ? { all: true, q, keep: false } : { all: true, q });
        };
      }
      for (const b of st.storeBody.querySelectorAll('[data-sell]')) {
        const it = c.items.find((x) => x.key === b.dataset.sell);
        let armed = 0;
        b.onclick = () => {
          const n = +b.dataset.n;
          const held = it ? keptOf(it) : 0;
          // Продаж рядка бере й відкладене (🔒 Толока, замовлення) — тоді питаємо вдруге, як віз (QA-3: «Продати» й «Усі N»
          // мовчки продавали вироби для Толоки).
          if (held > 0 && n > it.n - held && (!armed || Date.now() > armed)) {
            armed = Date.now() + ARM_MS;
            const was = b.textContent;
            b.style.minWidth = b.offsetWidth + 'px';
            b.textContent = 'Точно? 🔒';
            b.title = 'Підуть і відкладені: ' + keptText(it.kept);
            setTimeout(() => { if (Date.now() >= armed && b.isConnected) { b.textContent = was; b.style.minWidth = ''; b.title = ''; } }, ARM_MS + 50);
            return;
          }
          api.order(st, 'bazaar', { key: b.dataset.sell, n });
        };
      }
    }
  }

  // ---------- прокачка ремесла ----------

  /// Звідки береться кожне число місткості. Це та сама відповідь, що й на ⓘ чипів смуги: гравець мусить бачити,
  /// що сушарня, горно й комора ростуть, і чим саме.
  const UPS_STEP = {
    rack: ['Місткість сушарні', 'Сушарня: 8 місць + Гончарня за кожні 5 рівнів (разом до 20) + прокачка + «Друга сушарня».'],
    kiln: ['Місткість горна', 'Горно: 6 місць + Піч за кожні 10 рівнів (до 24) + майстер цеху + прокачка.'],
    store: ['Місткість комори', 'Комора: 200 виробів + прокачка.'],
  };
  const UPS_TAIL = 'Прокачка — це стіни майстерні, а не верстати: обпал її не палить.';
  const UPS_INFO = UPS_STEP.rack[1] + ' ' + UPS_STEP.kiln[1] + ' ' + UPS_STEP.store[1] + ' ' + UPS_TAIL;

  function paintUps(st, api) {
    const c = st.craft;
    if (!c || !st.upsBody) return;
    // Кличеться щокадру, а HTML міняється лише коли змінився рівень, стеля чи «по кишені»: інакше розгорнута ⓘ згорталась би сама.
    // Назва (приїжджає з каталогом) і «зараз: …» (росте разом із майстернею) — теж у підписі.
    const sig = (c.ups || []).map((u) => u.key + ':' + u.level + ':' + u.max + ':' + (st.shown >= u.price ? 1 : 0) + ':' + u.name + ':' + u.now).join(',');
    if (st.upsBody._sig === sig) return;
    st.upsBody._sig = sig;
    const esc = (x) => api.esc(st, x);
    const rows = (c.ups || []).map((u) => {
      const full = u.level >= u.max;
      const can = !full && st.shown >= u.price;
      return '<div class="clkw-up' + (full ? ' done' : '') + '" data-upkey="' + esc(u.key) + '">'
        + '<span class="clkw-uptxt"><span class="clkw-uphead"><b>' + esc(u.name) + '</b>'
        + '<span class="clkw-uplv">' + u.level + '/' + u.max + '</span></span>'
        + '<span class="muted small">' + esc(u.desc) + '</span>'
        + '<span class="small clkw-upnow">зараз: ' + esc(u.now) + '</span></span>'
        + (full
          ? '<span class="clk-price done">усе</span>'
          : '<button type="button" class="' + (can ? 'primary' : 'ghost') + ' small clkw-upbtn" data-up="' + esc(u.key) + '"'
            + (st.mine ? '' : ' disabled') + '>' + api.potsShort(u.price) + '</button>')
        + '</div>';
    }).join('');
    // Згортка (13.10): усе на межі — згорнута, поки гравець сам не розгорне; у заголовку — скільки ще можна прокачати.
    const open = (c.ups || []).filter((u) => u.level < u.max).length;
    if (!open && (c.ups || []).length) st.upsBody.dataset.fdef = '0'; else delete st.upsBody.dataset.fdef;
    const html = api.foldHead('🔧 Прокачати<span class="muted small"> · ' + (open ? 'ще ' + open + ' з ' + c.ups.length : 'усе на межі') + '</span>'
      + api.info(UPS_INFO)) + '<div class="clkw-ups">' + rows + '</div>';
    if (api.swap(st.upsBody, html)) {
      for (const b of st.upsBody.querySelectorAll('[data-up]')) {
        b.onclick = () => api.order(st, 'craft', { op: 'up', key: b.dataset.up });
      }
    }
  }

  // ---------- частина ----------

  HClicker.part({
    id: 'craft',
    order: 10,

    mount(st, api) {
      api.wareSvg = (ware, o) => wareSvg(api, ware, o);
      // Головна кнопка горна й кнопка «Далі» роблять одне й те саме — тож і код один.
      api.fireKiln = (o) => fireKiln(st, api, o);
      api.kilnLoad = () => loadNow(st, api);
      st.craftWheel = true;
      st.craftShelf = true;
      if (st.jugBox) st.jugBox._wear = null;
      st.craft = null;
      st.craftAt = Date.now();
      st.craftDone = 0;
      st.craftRackFree = 0;
      st.craftRackLen = -1;
      st.craftFxAt = 0;
      // Смуга «Шлях виробу»: чотири кроки й один рядок «Далі». Це єдиний путівник у грі — банера цілі,
      // рядка ремесла й окремих підказок більше нема.
      const bar = document.createElement('div');
      bar.className = 'clk-path';
      bar.innerHTML = '<div class="clk-steps"></div>'
        + '<div class="clkc-next"><span class="clk-nxico"></span>'
        + '<span class="clk-nxtxt"><b class="clk-nxtext"></b><span class="clk-nxsub small"></span></span>'
        + '<button type="button" class="primary clk-nxbtn" hidden></button>'
        + '<button type="button" class="ghost clk-nxbtn clk-nxbtn2" hidden></button><i class="clk-nxbar"><i></i></i></div>';
      st.stage.insertAdjacentElement('afterend', bar);
      st.craftUi = { el: bar, steps: bar.querySelector('.clk-steps'), nx: bar.querySelector('.clkc-next'),
        nxIco: bar.querySelector('.clk-nxico'), nxText: bar.querySelector('.clk-nxtext'), nxSub: bar.querySelector('.clk-nxsub'),
        nxBtn: bar.querySelector('.clk-nxbtn'), nxBar: bar.querySelector('.clk-nxbar i'), next: null, armed: 0,
        nxBtn2: bar.querySelector('.clk-nxbtn2'), next2: null, armed2: 0, nxTxt: bar.querySelector('.clk-nxtxt') };
      // Тап по тексту «Далі» прибирає підказку 💡 (вона живе на місці поради).
      st.craftUi.nxTxt.onclick = () => { const u = st.craftUi; if (u && u.tip) { u.tip = null; paintNext(st, api); } };
      st.craftUi.nxBtn.onclick = (ev) => runNext(st, api, ev);
      st.craftUi.nxBtn2.onclick = (ev) => runNext(st, api, ev, true);
      st.craftUi.tip = null;
      st.craftUi.tipT = 0;
      // «Ремесло» — одна вкладка на все ремесло: горно зверху, сушарня, замовлення сіл, комора з прокачкою. Замовлення —
      // над «Продати все» й «Прокачати» (v12, плейтест §2.4: були в самому низу), і на телефоні «Продати все» вже не стоїть
      // одразу під кнопками горна. Місця (data-slot) наповнюють горно (clicker-kiln.js) і ярмарок (clicker-fair.js).
      st.craftPane = api.tab(st, CRAFT_TAB, '🏺 Ремесло', 10);
      st.craftPane.innerHTML = '<div class="clkc-slot" data-slot="kiln"></div><div class="clkc-slot" data-slot="rack"></div>'
        + '<div class="clkc-slot" data-slot="fair"></div><div class="clkc-slot" data-slot="store"></div>';
      st.rackBody = document.createElement('div');
      st.rackBody.className = 'clkw-rackbox';
      api.slot(st, 'rack').appendChild(st.rackBody);
      st.storeBody = document.createElement('div');
      st.storeBody.className = 'clkw-store';
      st.storeBody.dataset.fold = 'c.store';
      api.slot(st, 'store').appendChild(st.storeBody);
      // Секція «🔧 Прокачати» — одразу під коморою: сушарня, горно й комора ростуть тут, а не десь у довідці.
      st.upsBody = document.createElement('section');
      st.upsBody.className = 'clkw-upsec';
      st.upsBody.dataset.fold = 'c.ups';
      api.slot(st, 'store').appendChild(st.upsBody);
      st.storeCds = [];
      // Ремесла ще нема, поки нема чого ремеслити: новачок бачить лише Майстерню, а смуга веде його сама.
      // Партія в горні — теж ремесло: перший обпал забирає з сушарні все, а обпаленого й комори ще нема, і без цього
      // вкладка ховалась посеред першого горна — гравця перекидало в Майстерню, і горно догоряло з 0 % (хотфікс B2).
      api.showWhen(st, CRAFT_TAB, (st2, v2) => {
        const c2 = st2.craft;
        const k = (v2 && v2.kiln) || st2.kView;
        return !!c2 && (c2.rack.length > 0 || c2.fired > 0 || c2.items.length > 0
          || (!!k && ((k.batch && k.batch.length > 0) || k.state === 'burning' || k.state === 'cooling')));
      });
    },

    update(st, v, api) {
      const c = v.craft;
      if (!c) return;
      st.craft = {
        ware: c.ware, work: c.work || 0, need: c.need || 1, apprentice: c.apprentice || 0, rackFull: !!c.rackFull,
        rack: (c.rack || []).map((r) => ({ ware: r.ware, clay: r.clay || '', dryAt: Date.parse(r.dryAt) || 0 })),
        // price — що справді дасть базар: Візок хати (+10 %) множить плату (QA-3: ціни в коморі були на ~10 % нижчі).
        // value лишається ціною виробу — нею рахують дарунки, віз і замовлення інших частин.
        rackSize: c.rackSize || 8, wares: waresOf(st, c.wares || []), bazaar: c.bazaarMult > 0 ? c.bazaarMult : 1,
        items: (c.items || []).map((it) => Object.assign({}, it, { price: it.value * (c.bazaarMult > 0 ? c.bazaarMult : 1) })),
        storeCap: c.storeCap || 200,
        ups: upsOf(st, c.ups || []), formed: c.formed || 0, fired: c.fired || 0,
      };
      st.craftAt = Date.now();
      st.craftDone = 0;
      st.craftRackFree = st.craft.rackSize - st.craft.rack.length;
      // Сервер доліпив виріб сам (підмайстри, або наша пачка долетіла раніше, ніж ми передбачили) — ефект, якщо ми ще не малювали.
      if (st.craftRackLen >= 0 && st.craft.rack.length > st.craftRackLen && Date.now() - st.craftFxAt > 2500) {
        api.sparks(st, st.fx, 6, false, 50, 62);
      }
      st.craftRackLen = st.craft.rack.length;
      const count = st.craft.items.reduce((s, it) => s + it.n, 0);
      api.tabNote(st, CRAFT_TAB, 'store', count ? '📦' + count : '', 3);
      st.shelfJugs._craft = null;
      paintPath(st, api);
      paintStore(st, api);
      paintUps(st, api);
    },

    frame(st, api) {
      if (api.guardOn(st)) return;
      paintWheel(st, api);
    },

    slow(st, api, now) {
      paintShelf(st, api);
      paintPath(st, api);
      maybeTip(st, api);
      if (st.tab === CRAFT_TAB) {
        // Глеки набігають і без нового виду — кнопки прокачки мусять світлішати самі.
        paintUps(st, api);
        for (const el of st.storeCds) {
          const left = +el.dataset.at - now;
          const t = left > 0 ? api.mmss(left) : el.dataset.done;
          if (el.textContent !== t) el.textContent = t;
        }
        // Сирець висох — переставити картку в «сухий».
        if (st.craft && st.craft.rack.some((r) => r.dryAt <= now) && st.storeBody._dry !== st.craft.rack.filter((r) => r.dryAt <= now).length) {
          st.storeBody._dry = st.craft.rack.filter((r) => r.dryAt <= now).length;
          paintStore(st, api);
        }
      }
    },

    unmount(st) {
      if (st.craftUi) clearTimeout(st.craftUi.tipT);
      st.craftUi = null;
      st.craftPane = null;
      st.upsBody = null;
      st.rackBody = null;
    },
  });
})();
