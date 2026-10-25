/* «Чумаки-далекобійники» (план: D:\or-wt\_fura\PLAN.md, spec: docs/games/specs/fura.md).
   Картка столу лише запускає гру: усе важке — 3D (three.js), фізика (Rapier, WASM), кермо — живе в ES-модулях
   web/games/fura/ і вантажиться через import() лише після «Їхати». Гра йде на весь екран поверх сайту
   (стеля .gcanvas 560 px для фури замала), ✕ повертає на цю саму картку.

   Пад: поки гра відкрита, вона сама читає стіки й курки (fura/input.js), тож pad.js не має ні гортати сторінку,
   ні тиснути кнопки сайту — `dirs` вмикається лише на час гри, а `on` з'їдає всі кнопки. Кермо pad.js і так
   відсіює, його гра читає прямо з navigator.getGamepads().

   Картка — один вхід «🚚 Чумаки» (C1, specs/fura.md «Столи й зіткнення наживо»): зверху вибір режиму для «Їхати»
   (👻 привиди / 💥 зіткнення — опція столу collide), велика «🚚 Їхати», під нею «Хто зараз у рейсі» з «Їхати разом»,
   унизу тихо «🏋 Полігон» і «Як керувати». «Їхати»: сиджу за столом Чумаків з цим режимом — туди (цей стіл — одразу
   гра, інший — відкриваємо й гра стартує сама); режим інший чи не сиджу — CreateRoom з опцією (сидиш за іншим —
   каркас спершу спитає «встати?»). «Їхати разом» — HGames.sitAt → JoinRoom (посеред рейсу сервер садить сам, бо гра
   LateJoin; «Сісти» на playing-столі каркас не показує, тож кнопку дає картка). Після «Їхати», «Їхати разом» і свого
   свіжого столу з «+ Стіл» каталогу гра відкривається сама. Режим посеред гри не міняється: інший режим — новий стіл.
   Кадри привидів — у гру через міст. */
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="4" width="9" height="7" rx="1" fill="var(--clay, #b5582f)"/>'
    + '<path d="M10 6h2.6l2.4 2.6V11h-5z" fill="var(--accent, #d08a2c)"/>'
    + '<circle cx="4" cy="12" r="1.6" fill="var(--muted, #555)"/><circle cx="12" cy="12" r="1.6" fill="var(--muted, #555)"/></svg>';

  let game = null;        // запущена гра: { stop() } з fura/main.js
  let ctxNow = null;      // останній ctx столу: з нього міст бере вид і шле дії
  // Міст «стіл ↔ 3D»: тихі дії (відповідь гра показує сама — тости сайту сховані під грою), телеметрія Input і вид
  const bridge = {
    tell: (a, p) => (ctxNow ? ctxNow.tell(a, p) : Promise.resolve(null)),
    input: (a, p) => { if (ctxNow) ctxNow.input(a, p); },
    view: () => (ctxNow ? ctxNow.view : null),
    onView: null,
    onFrame: null,
  };
  let starting = false, gameMode = '';
  let autoStart = null;     // стіл, за який щойно сіли чи який поставили з картки: щойно там сидимо — одразу в край
  let paintT = 0;
  const FRESH_MS = 90000;   // свій стіл, поставлений не раніше — щойно з «+ Стіл» каталогу: гра відкривається сама
  const esc = (t) => String(t == null ? '' : t).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const nicksOf = (r) => (r.seats || []).map((x) => (typeof x === 'string' ? x : x && x.nick)).filter(Boolean);
  const MODES = { ghosts: '👻 привиди', crash: '💥 зіткнення' };
  const MODE_TILE = {
    ghosts: ['👻', 'Привиди', 'крізь чужі фури — спокійно їдеш колоною'],
    crash: ['💥', 'Зіткнення', 'фури б\'ються; біля боксів — крізь'],
  };
  const modeOf = (r) => (r && r.options && r.options.collide === 'crash' ? 'crash' : 'ghosts');
  const same = (a, b) => String(a || '').trim().toLowerCase() === String(b || '').trim().toLowerCase();
  const stored = () => { try { return localStorage.getItem('fura.collide') === 'crash' ? 'crash' : 'ghosts'; } catch { return 'ghosts'; } };
  let want = null;          // режим, обраний у картці (null — як у мого столу чи як минулого разу)

  /** Мій живий стіл Чумаків: цей, якщо сиджу тут, інакше — з лобі (сервер тримає за одним столом). Перерваний
   *  (перезапуск сайту, status finished) — не в рейсі: «Їхати» ставить новий. */
  function seatRoom(ctx) {
    const live = (r) => r && r.status === 'playing';
    if (ctx.mine) return live(ctx.room) ? ctx.room : null;
    const me = ctx.me && ctx.me.nick;
    if (!me) return null;
    return (HGames.rooms || []).find((r) => r.game === 'fura' && live(r) && r.maxPlayers > 1 && nicksOf(r).some((n) => same(n, me))) || null;
  }

  function card(root) {
    root.innerHTML = '<div class="fura-card">'
      + '<div class="fura-title">🚚 Чумаки-далекобійники <span class="fura-stage">колона до 8 фур · версія 0.0.3</span></div>'
      + '<div class="fura-modes" role="radiogroup" aria-label="Чужі фури"></div>'
      + '<div class="fura-main"></div>'
      + '<div class="fura-tables"></div>'
      + '<div class="fura-row fura-btns"><button class="btn fura-quiet" type="button" data-do="polygon" title="Майданчик з конусами: потренуватись чіплятись заднім ходом, без замовлень і без інших фур">🏋 Полігон</button></div>'
      + '<details class="fura-more"><summary>Як керувати й куди їхати</summary>'
      + '<p><b>Чумацький край</b> — пагорби, річка з мостами, траса, сільські дороги й ґрунтовки між депо, Гончарівкою, '
      + 'Цегельнею, Медовим, Соляним складом і Полтавкою. Бери замовлення, чіпляй причіп і вези — сам чи колоною з друзями: '
      + 'колонне замовлення дає +15 % тим, хто здасть разом.</p>'
      + '<ul class="fura-dev"><li>🛞 <b>Кермо</b> (G29 та інші) — при першому вході гра попросить відкалібрувати</li>'
      + '<li>🎮 <b>Пад / Steam Deck</b> — лівий стік кермо, R2 газ, L2 гальмо, Ⓐ зчепити</li>'
      + '<li>⌨ <b>Клавіатура</b> — W/S газ і гальмо, A/D кермо, T зчепити, C камера</li></ul></details>'
      + '<div class="fura-err muted small" role="status"></div></div>';
    root.addEventListener('click', (e) => {
      const b = e.target.closest('button[data-do]');
      if (b && !b.disabled) press(root, b);
    });
    paint(root);
  }

  /** Режим, вхід і живі столи — з поточного ctx і лобі; перемальовуємо лише, коли змінилось. */
  function paint(root) {
    const ctx = ctxNow, main = root.querySelector('.fura-main'), tables = root.querySelector('.fura-tables');
    if (!ctx || !main) return;
    const room = ctx.room || {};
    const mineR = seatRoom(ctx), has = mineR ? modeOf(mineR) : null;
    const wm = want || has || stored();

    const modes = root.querySelector('.fura-modes');
    const m = Object.keys(MODE_TILE).map((k) => {
      const [ico, name, sub] = MODE_TILE[k];
      return '<button class="fura-mode-b" type="button" role="radio" data-do="mode" data-mode="' + k + '" aria-checked="' + (k === wm) + '">'
        + '<span class="i">' + ico + '</span><span class="t"><b>' + name + '</b><small>' + sub + '</small></span></button>';
    }).join('');
    if (modes._html !== m) { modes.innerHTML = m; modes._html = m; }

    // велика «Їхати» й рядок під нею: куди саме поїду
    let label = '🚚 Їхати', sub, go = 'own';
    if (mineR && has === wm) {
      go = mineR.id === room.id ? 'kray' : 'mine';
      const others = nicksOf(mineR).filter((n) => !same(n, ctx.me && ctx.me.nick));
      sub = 'твій стіл · ' + MODES[has] + (others.length ? ' · з тобою: ' + others.map(esc).join(', ') : ' · друзі приєднаються тут, у «Хто зараз у рейсі»');
    } else if (mineR) {
      label = '🚚 Їхати — новий стіл ' + MODES[wm].split(' ')[0];
      sub = 'твій стіл зараз — ' + MODES[has] + '; новий буде ' + MODES[wm] + ' (зі старого встанеш)';
    } else sub = 'свій стіл · ' + MODES[wm] + ' · друзі приєднаються до тебе';
    const mm = '<button class="btn primary fura-go fura-go-main" type="button" data-do="go" data-go="' + go + '"'
      + (go === 'mine' ? ' data-room="' + esc(mineR.id) + '"' : '') + '>' + label + '</button>'
      + '<div class="fura-sub muted small">' + sub + '</div>';
    if (main._html !== mm) { main.innerHTML = mm; main._html = mm; }

    // хто зараз у рейсі: столи Чумаків з людьми, крім мого; той, що дивлюсь, — першим
    const seen = new Set(), list = [];
    const add = (r) => { if (r && r.game === 'fura' && r.status === 'playing' && r.maxPlayers > 1 && !seen.has(r.id) && (!mineR || r.id !== mineR.id) && nicksOf(r).length) { seen.add(r.id); list.push(r); } };
    if (!ctx.mine) add(room);
    for (const r of HGames.rooms || []) add(r);
    let t = '<div class="fura-tables-h">Хто зараз у рейсі</div>';
    if (!list.length) t += '<div class="muted small fura-none">Поки ніхто не їде. Постав свій стіл — друзі побачать його тут і приєднаються.</div>';
    else t += list.map((r) => {
      const ns = nicksOf(r), free = ns.length < r.maxPlayers, md = modeOf(r);
      return '<div class="fura-table' + (r.id === room.id ? ' here' : '') + '">'
        + '<span class="fura-tmode ' + md + '" title="' + MODES[md] + '">' + MODES[md] + '</span>'
        + '<span class="fura-tnick">' + ns.map(esc).join(', ') + ' <span class="muted small">' + ns.length + '/' + r.maxPlayers + '</span></span>'
        + (free ? '<button class="btn fura-go2" type="button" data-do="sit" data-room="' + esc(r.id) + '">Їхати разом</button>'
          : '<span class="muted small">повна колона</span>') + '</div>';
    }).join('');
    if (tables._html !== t) { tables.innerHTML = t; tables._html = t; }
  }

  async function press(root, b) {
    const k = b.dataset.do, err = root.querySelector('.fura-err');
    if (err) err.textContent = '';
    if (k === 'polygon') return start(root, b, 'polygon');
    if (k === 'mode') {
      want = b.dataset.mode;
      try { localStorage.setItem('fura.collide', want); } catch { /* пусто */ }
      return paint(root);
    }
    if (k === 'go' && b.dataset.go === 'kray') return start(root, b, 'kray');
    if (k === 'go' && b.dataset.go === 'mine') { autoStart = b.dataset.room; HGames.openAt(b.dataset.room); return; }
    if (k === 'sit') {
      // Кнопку не вимикаємо самі: sitAt крутить «підсідаю…» через busy (app.js), а той на вимкненій кнопці мовчки
      // нічого не робить — JoinRoom так і не йшов (25.10, живий баг зі зведення). Спершу autoStart: вид «я за столом»
      // може прийти раніше за відповідь на JoinRoom.
      autoStart = b.dataset.room;
      const r = await HGames.sitAt(b.dataset.room, b);
      if (!r || !r.ok) autoStart = null;
      return;
    }
    if (k !== 'go') return;
    const label = b.textContent;
    b.disabled = true;
    b.textContent = '⏳ Ставимо стіл…';
    try {
      const r = await HGames.call('CreateRoom', 'fura', { collide: want || (seatRoom(ctxNow) ? modeOf(seatRoom(ctxNow)) : stored()) });
      if (r && r.ok && r.roomId) { autoStart = r.roomId; HGames.openAt(r.roomId); }
      else if (err && r && r.message) err.textContent = r.message;
    } finally { b.disabled = false; b.textContent = label; }
  }

  async function start(root, btn, mode) {
    if (game || starting) return;
    starting = true;
    const err = root.querySelector('.fura-err');
    const label = btn && btn.textContent;
    root.querySelectorAll('.fura-go, .fura-quiet').forEach((b) => { b.disabled = true; });
    if (btn) btn.textContent = '⏳ Заводимо…';
    if (err) err.textContent = '';
    try {
      const mod = await import('/games/fura/main.js');
      bridge.onView = null;
      bridge.onFrame = null;
      gameMode = mode;
      game = await mod.start({ onExit: () => { stop(); focusGo(root); }, mode, server: mode === 'kray' ? bridge : null });
    } catch (e) {
      console.error('[fura]', e);
      if (err) err.textContent = 'Не завелось: ' + (e && e.message ? e.message : e);
    } finally {
      starting = false;
      root.querySelectorAll('.fura-go, .fura-quiet').forEach((b) => { b.disabled = false; });
      if (btn && btn.isConnected) btn.textContent = label;
    }
  }

  function stop() {
    const g = game;
    game = null;
    bridge.onView = null;
    bridge.onFrame = null;
    if (g) { try { g.stop(); } catch (e) { console.warn('[fura] stop', e); } }
  }

  /** Фокус на «Їхати» (пад і кермо: Ⓐ одразу їде), якщо людина ніде не пише. */
  function focusGo(root) {
    const a = document.activeElement;
    if (a && a !== document.body && (a.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName))) return;
    const b = root.querySelector('.fura-go-main');
    if (b) try { b.focus({ preventScroll: true }); } catch { /* пусто */ }
  }

  /** Свій стіл, щойно поставлений (з «+ Стіл» каталогу): господар — я, мені одному, свіжий і ще не відкривався тут. */
  function freshOwn(ctx) {
    const r = ctx.room;
    if (!ctx.mine || !r || !same(r.host, ctx.me && ctx.me.nick) || nicksOf(r).length > 1) return false;
    const at = Date.parse(r.createdAt || '');
    if (!(at > 0) || Math.abs(Date.now() - at) > FRESH_MS) return false;
    try {
      if (sessionStorage.getItem('fura.auto.' + r.id)) return false;
      sessionStorage.setItem('fura.auto.' + r.id, '1');
    } catch { return false; }
    return true;
  }

  /** Сіли за стіл (чи поставили свій) — одразу в край. */
  function maybeAuto(root, ctx) {
    if (game || starting || !ctx.room || !ctx.mine || ctx.room.status !== 'playing') return;
    if (autoStart === ctx.room.id || freshOwn(ctx)) {
      autoStart = null;
      start(root, root.querySelector('.fura-go-main'), 'kray');
    }
  }

  HGames.register({
    id: 'fura',
    icon: ICON,
    added: '2026-10-25',
    pad: {
      get dirs() { return !!game; },
      rstick: true,
      exit: false,
      on() { return !!game; },
      hint: '{dpad} кермо · R2 газ · L2 гальмо · {a} зчепити',
    },
    mount(root, ctx) {
      ctxNow = ctx; want = null; card(root);   // новий стіл — режим знову як у мого столу
      clearInterval(paintT);
      paintT = setInterval(() => { if (root.isConnected) paint(root); }, 2000);   // лобі міняється й без нашого виду
      focusGo(root);
      maybeAuto(root, ctx);
    },
    update(root, ctx) {
      ctxNow = ctx;
      if (game && bridge.onView) { try { bridge.onView(ctx.view); } catch (e) { console.warn('[fura] view', e); } }
      paint(root);
      maybeAuto(root, ctx);
    },
    frame(root, ctx, f) {
      if (game && bridge.onFrame && f && Array.isArray(f.p)) { try { bridge.onFrame(f); } catch (e) { console.warn('[fura] frame', e); } }
    },
    unmount() { clearInterval(paintT); paintT = 0; stop(); },
    status() { return game ? (gameMode === 'kray' ? 'у рейсі' : 'на полігоні') : ''; },
  });
})();
