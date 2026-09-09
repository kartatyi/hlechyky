/*
  Глек-слово. Правила, слово дня і оцінка спроб — на сервері (Impl/Wordle.cs); тут лише малювання
  дошки й наміри: набрав п'ять літер, натиснув Enter — пішов Act('guess', { word }).

  Вид із сервера: { day, no, rows: [{ word, marks }], attempts, max, solved, failed,
                    answer, keys: { 'а': 'G'|'Y'|'B' }, share, noWords }.
  marks — рядок із п'яти літер: G (на місці), Y (є, але не тут), B (нема).
*/
(() => {
  const LEN = 5;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="2.4" width="4.2" height="4.2" rx="1.2" fill="var(--ok)"/>'
    + '<rect x="5.9" y="2.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/>'
    + '<rect x="10.8" y="2.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/>'
    + '<rect x="1" y="9.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/>'
    + '<rect x="5.9" y="9.4" width="4.2" height="4.2" rx="1.2" fill="var(--accent)"/>'
    + '<rect x="10.8" y="9.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/></svg>';

  /// Українська абетка без апострофа: рівно те, що приймає Words.Normalize на сервері.
  const ALPHABET = 'абвгґдеєжзиіїйклмнопрстуфхцчшщьюя';
  const CLS = { G: 'g', Y: 'y', B: 'b' };

  /// «за 1 спробу», «за 3 спроби», «за 6 спроб» — інакше рядок статусу читається як телеграма.
  function tries(n) {
    const t = n % 100, o = n % 10;
    if (t > 10 && t < 20) return n + ' спроб';
    if (o === 1) return n + ' спробу';
    if (o >= 2 && o <= 4) return n + ' спроби';
    return n + ' спроб';
  }

  /// Скільки лишилось до київської півночі. Зона береться з браузера; якщо він її не знає
  /// (старий движок без бази зон) — рахуємо як UTC+3, це та сама підстраховка, що й на сервері.
  function msToMidnight() {
    const now = new Date();
    let h, m, s;
    try {
      const parts = new Intl.DateTimeFormat('en-GB', {
        timeZone: 'Europe/Kyiv', hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit',
      }).formatToParts(now);
      const at = (type) => +((parts.find((p) => p.type === type) || {}).value || 0);
      h = at('hour') % 24; m = at('minute'); s = at('second');
    } catch (e) {
      const k = new Date(now.getTime() + 3 * 3600e3);
      h = k.getUTCHours(); m = k.getUTCMinutes(); s = k.getUTCSeconds();
    }
    return (24 * 3600 - (h * 3600 + m * 60 + s)) * 1000;
  }

  function nextWordIn() {
    const left = msToMidnight();
    const h = Math.floor(left / 3600e3);
    const m = Math.floor((left % 3600e3) / 60e3);
    return 'Наступне слово через ' + (h ? h + ' год ' + m + ' хв' : Math.max(1, m) + ' хв');
  }

  /// Копіювання без clipboard API теж має працювати: сайт відкривають і по локальній адресі,
  /// а там navigator.clipboard браузер не дає.
  function copy(text, ctx) {
    const done = () => ctx.toast('Скопійовано', 'ok');
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, () => fallback(text, ctx));
      return;
    }
    fallback(text, ctx);
  }
  function fallback(text, ctx) {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    let ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    ta.remove();
    ctx.toast(ok ? 'Скопійовано' : 'Не вийшло скопіювати', ok ? 'ok' : 'err');
  }

  /// Тіло картки за id кімнати: onKey приходить від каркаса з ctx, а не з DOM, і корінь треба звідкись узяти.
  const roots = {};

  function state(root) {
    // flipFrom — з якого ряду починається переворот; клас віддаємо самій сітці, а не чіпляємо
    // поверх неї (див. paint)
    if (!root._wordle) {
      root._wordle = { draft: '', flipFrom: null, bad: false, sending: false, timer: 0, ctx: null };
    }
    return root._wordle;
  }

  const over = (v) => !!(v && (v.solved || v.failed));

  // ------------------------------------------------------------------------------------ малювання

  function paint(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    if (ctx.room) roots[ctx.room.id] = root;
    const v = ctx.view || {};
    const rows = v.rows || [];
    const max = v.max || 6;
    // перший малюнок нічого не перевертає: інакше після F5 уся дошка робила б сальто. Усе, що
    // відкрилось уже на наших очах, переворот дістає — і лишає клас назавжди: анімація одноразова, а
    // ui.grid переписує className, коли той не збігається з бажаним, тож клас, доданий поверх сітки,
    // злітав би з першої ж наступної набраної літери й обривав переворот на середині.
    if (st.flipFrom === null) st.flipFrom = rows.length;

    HGames.ui.grid(root, {
      cols: LEN,
      rows: max,
      cls: 'wtiles',
      cell: (i) => {
        const r = Math.floor(i / LEN), c = i % LEN;
        const row = rows[r];
        if (row) {
          return {
            html: ctx.esc(row.word[c] || ''),
            cls: (CLS[(row.marks || '')[c]] || 'b') + (r >= st.flipFrom ? ' flip' : ''),
            disabled: true,
          };
        }
        const drafting = r === rows.length && !over(v) && ctx.mine;
        const ch = drafting ? st.draft[c] : '';
        return { html: ctx.esc(ch || ''), cls: (ch ? 'typed' : '') + (drafting && st.bad ? ' bad' : ''), disabled: true };
      },
    });

    HGames.ui.keyboardUa(root, (k) => press(root, k), v.keys || {});
    foot(root, ctx, v);
  }

  /// Підвал картки: коли день дограно — «Скопіювати результат» і скільки чекати нового слова.
  function foot(root, ctx, v) {
    let el = root.querySelector(':scope > .wfoot');
    if (!el) {
      el = document.createElement('div');
      el.className = 'wfoot';
      root.appendChild(el);
    }
    if (v.noWords) {
      const html = '<div class="muted small">Словника на цьому сервері нема — сьогодні без слова.</div>';
      if (el.innerHTML !== html) el.innerHTML = html;
      return;
    }
    if (!over(v)) {
      if (el.innerHTML !== '') el.innerHTML = '';
      return;
    }
    const html = (v.share ? '<button class="ghost" data-copy>Скопіювати результат</button>' : '')
      + '<div class="muted small wnext">' + ctx.esc(nextWordIn()) + '</div>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      const b = el.querySelector('[data-copy]');
      if (b) b.onclick = () => copy(v.share || '', ctx);
    }
  }

  // -------------------------------------------------------------------------------------- ввід

  /// Повертає true, лише якщо клавіша справді щось зробила: за цим каркас вирішує, гасити подію чи ні
  /// (див. onKey), а гасити зайве не можна — Enter на дограному дні має тиснути «Скопіювати результат».
  function press(root, key) {
    const st = state(root);
    const ctx = st.ctx;
    if (!ctx || !ctx.mine) return false;
    const v = ctx.view || {};
    if (v.noWords || over(v)) return false;

    if (key === 'Enter') return submit(root);
    if (key === 'Backspace') {
      if (!st.draft) return false;
      st.draft = st.draft.slice(0, -1);
      paint(root, ctx);
      return true;
    }
    const ch = String(key || '').toLowerCase();
    if (ch.length !== 1 || ALPHABET.indexOf(ch) < 0) return false;
    if (st.draft.length >= LEN) return false;
    st.draft += ch;
    paint(root, ctx);
    return true;
  }

  function submit(root) {
    const st = state(root);
    const ctx = st.ctx;
    if (!ctx || st.sending || !st.draft) return false;
    if (st.draft.length < LEN) { shake(root); ctx.toast('Треба п\'ять літер', 'err'); return true; }
    const word = st.draft;
    st.sending = true;
    ctx.act('guess', { word }).then((r) => {
      st.sending = false;
      if (r && r.ok) st.draft = '';
      else shake(root);
      paint(root, st.ctx || ctx);
    }, () => { st.sending = false; });
    return true;
  }

  function shake(root) {
    const st = state(root);
    if (st.bad) return;
    st.bad = true;
    paint(root, st.ctx);
    setTimeout(() => {
      st.bad = false;
      if (st.ctx) paint(root, st.ctx);
    }, 420);
  }

  // ---------------------------------------------------------------------------------- реєстрація

  HGames.register({
    id: 'wordle',
    icon: ICON,
    seatNames: ['слово'],
    seatClass: ['x'],

    mount(root, ctx) {
      const st = state(root);
      st.flipFrom = null;   // те, що вже стоїть на дошці, після F5 сальто не робить
      paint(root, ctx);
      // відлік до нового слова тікає сам; хвилини вистачає — година й хвилини й так змінюються повільно
      st.timer = setInterval(() => {
        const el = root.querySelector(':scope > .wfoot .wnext');
        if (el) el.textContent = nextWordIn();
      }, 30000);
    },

    update(root, ctx) { paint(root, ctx); },

    onKey(e, ctx) {
      // літери читаємо з e.key, а не з e.code: розкладка тут і є змістом гри
      const root = ctx.room && roots[ctx.room.id];
      if (!root || !ctx.mine) return false;
      // віддаємо рівно те, що сталось: на true каркас робить preventDefault, а він гасить і Enter
      // на сфокусованій кнопці картки
      if (e.key === 'Enter' || e.key === 'Backspace') return press(root, e.key);
      const ch = String(e.key || '').toLowerCase();
      if (ch.length !== 1 || ALPHABET.indexOf(ch) < 0) return false;
      return press(root, ch);
    },

    status(ctx) {
      const v = ctx.view;
      if (!v) return '';
      if (v.noWords) return 'Словника нема — сьогодні без слова';
      if (v.solved) return 'Слово дня взято за ' + tries(v.attempts);
      if (v.failed) return 'Слово було: ' + String(v.answer || '').toUpperCase();
      if (!ctx.mine) return 'Дивишся збоку';
      return 'Спроба ' + Math.min(v.attempts + 1, v.max) + ' з ' + v.max;
    },

    unmount(root, ctx) {
      const st = root._wordle;
      if (st && st.timer) clearInterval(st.timer);
      if (ctx && ctx.room) delete roots[ctx.room.id];
      root._wordle = null;
    },
  });
})();
