/* «Чумаки-далекобійники» — етап 0, полігон (план: D:\or-wt\_fura\PLAN.md).
   Картка столу лише запускає гру: усе важке — 3D (three.js), фізика (Rapier, WASM), кермо — живе в ES-модулях
   web/games/fura/ і вантажиться через import() лише після «На полігон». Гра йде на весь екран поверх сайту
   (стеля .gcanvas 560 px для фури замала), ✕ повертає на картку.

   Пад: поки гра відкрита, вона сама читає стіки й курки (fura/input.js), тож pad.js не має ні гортати сторінку,
   ні тиснути кнопки сайту — `dirs` вмикається лише на час гри, а `on` з'їдає всі кнопки. Кермо pad.js і так
   відсіює, його гра читає прямо з navigator.getGamepads().

   Етап 2 — колона: стіл до 8 фур. Сидиш за цим столом — «🗺 Чумацький край» їде за ним (це й є свій стіл); дивишся
   чужий — «приєднатися» або «свій стіл» (CreateRoom). Приєднатися — тим самим шляхом, яким каркас садить за стіл
   (HGames.sitAt → JoinRoom; посеред рейсу сервер садить сам, бо гра LateJoin), лише кнопку дає картка: «Сісти» на
   playing-столі каркас не показує. Нижче — живі столи fura з лобі (HGames.rooms). Кадри привидів — у гру через міст.

   2c — опція столу collide: «👻 привиди» (типово) чи «💥 зіткнення». Обирається при створенні столу (попап лобі або
   перемикач у картці) і посеред гри не міняється: інший режим — це новий стіл (CreateRoom з опцією; сидиш за цим —
   каркас спершу спитає «встати?»). Режим столу видно в шапці столу (каркас підписує нетипову опцію) і 💥 у списку живих. */
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
  const esc = (t) => String(t == null ? '' : t).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const nicksOf = (r) => (r.seats || []).map((x) => (typeof x === 'string' ? x : x && x.nick)).filter(Boolean);
  const MODES = { ghosts: '👻 привиди', crash: '💥 зіткнення' };
  const modeOf = (r) => (r && r.options && r.options.collide === 'crash' ? 'crash' : 'ghosts');
  // який режим обрано в картці для нового столу: спершу — як у столу, за яким сидиш (чи як обирав минулого разу)
  let want = null;
  const wantMode = (room, mine) => want || (mine ? modeOf(room) : (() => { try { return localStorage.getItem('fura.collide') === 'crash' ? 'crash' : 'ghosts'; } catch { return 'ghosts'; } })());

  function card(root) {
    root.innerHTML = '<div class="fura-card">'
      + '<div class="fura-title">🚚 Чумаки-далекобійники <span class="fura-stage">колона до 8 фур · версія 0.3</span></div>'
      + '<p><b>Чумацький край</b> — пагорби, річка з мостами, траса, сільські дороги й ґрунтовки між депо, Гончарівкою, '
      + 'Цегельнею, Медовим, Соляним складом і Полтавкою. Бери замовлення, чіпляй причіп і вези — сам чи колоною з друзями: '
      + 'колонне замовлення дає +15 % тим, хто здасть разом. <b>Полігон</b> — майданчик, щоб потренуватись чіплятись заднім ходом.</p>'
      + '<ul class="fura-dev"><li>🛞 <b>Кермо</b> (G29 та інші) — при першому вході гра попросить відкалібрувати</li>'
      + '<li>🎮 <b>Пад / Steam Deck</b> — лівий стік кермо, R2 газ, L2 гальмо, Ⓐ зчепити</li>'
      + '<li>⌨ <b>Клавіатура</b> — W/S газ і гальмо, A/D кермо, T зчепити, C камера</li></ul>'
      + '<div class="fura-row fura-mode"></div>'
      + '<div class="fura-row fura-btns"></div>'
      + '<div class="fura-tables"></div>'
      + '<div class="fura-err muted small"></div></div>';
    root.addEventListener('click', (e) => {
      const b = e.target.closest('button[data-do]');
      if (b && !b.disabled) press(root, b);
    });
    paint(root);
  }

  /** Кнопки й живі столи — з поточного ctx і лобі; перемальовуємо лише, коли змінилось. */
  function paint(root) {
    const ctx = ctxNow, btns = root.querySelector('.fura-btns'), tables = root.querySelector('.fura-tables');
    if (!ctx || !btns) return;
    const room = ctx.room || {}, here = nicksOf(room);
    const watch = !ctx.mine && room.maxPlayers > 1;          // дивлюсь чужий стіл
    const full = here.length >= (room.maxPlayers || 1);
    const has = modeOf(room), wm = wantMode(room, ctx.mine);
    // перемикач режиму: за своїм столом — інший режим означає новий стіл; дивлюсь чужий — режим мого нового столу
    const mode = root.querySelector('.fura-mode');
    const m = '<span class="muted small">Чужі фури:</span>' + Object.keys(MODES).map((k) => '<button class="btn" type="button" data-do="mode" data-mode="' + k
      + '" aria-pressed="' + (k === wm) + '"' + (k === wm ? ' style="outline:2px solid var(--accent, #d08a2c);font-weight:700"' : '') + '>' + MODES[k] + '</button>').join('')
      + '<span class="muted small">' + (ctx.mine || watch ? 'за цим столом — ' + MODES[has] : '') + '</span>';
    if (mode && mode._html !== m) { mode.innerHTML = m; mode._html = m; }
    let b = '';
    if (watch) {
      if (!full) b += '<button class="btn fura-go" type="button" data-do="join" data-room="' + esc(room.id) + '">🚚 Приєднатися до колони'
        + (here.length ? ' (' + here.map(esc).join(', ') + ')' : '') + (has === 'crash' ? ' 💥' : '') + '</button>';
      b += '<button class="btn fura-go' + (full ? '' : ' fura-go2') + '" type="button" data-do="own">🗺 Свій стіл у краю' + (wm === 'crash' ? ' 💥' : '') + '</button>';
    } else if (ctx.mine && wm !== has) b += '<button class="btn fura-go" type="button" data-do="own">🗺 Новий стіл: ' + MODES[wm] + '</button>';
    else b += '<button class="btn fura-go" type="button" data-do="kray">🗺 Чумацький край' + (has === 'crash' ? ' 💥' : '') + '</button>';
    b += '<button class="btn fura-go fura-go2" type="button" data-do="polygon">🚧 Полігон</button>';
    if (btns._html !== b) { btns.innerHTML = b; btns._html = b; }

    // живі столи fura, крім цього
    let t = '';
    const list = (HGames.rooms || []).filter((r) => r.game === 'fura' && r.id !== room.id && r.maxPlayers > 1 && nicksOf(r).length);
    if (list.length) {
      t = '<div class="fura-tables-h">🚚 У рейсі зараз</div>' + list.map((r) => {
        const ns = nicksOf(r), free = ns.length < r.maxPlayers;
        return '<div class="fura-table"><span>' + (modeOf(r) === 'crash' ? '<span title="зіткнення">💥</span> ' : '') + ns.map(esc).join(', ') + ' <span class="muted small">' + ns.length + '/' + r.maxPlayers + '</span></span>'
          + (free ? '<button class="btn" type="button" data-do="sit" data-room="' + esc(r.id) + '">приєднатися</button>' : '<span class="muted small">повна колона</span>') + '</div>';
      }).join('');
    }
    if (tables._html !== t) { tables.innerHTML = t; tables._html = t; }
  }

  async function press(root, b) {
    const k = b.dataset.do, err = root.querySelector('.fura-err');
    if (err) err.textContent = '';
    if (k === 'kray' || k === 'polygon') return start(root, b, k);
    if (k === 'mode') {
      want = b.dataset.mode;
      try { localStorage.setItem('fura.collide', want); } catch { /* пусто */ }
      return paint(root);
    }
    if (k === 'join' || k === 'sit') {
      // Кнопку не вимикаємо самі: sitAt крутить «підсідаю…» через busy (app.js), а той на вимкненій кнопці мовчки
      // нічого не робить — JoinRoom так і не йшов (25.10, живий баг зі зведення). Спершу autoStart: вид «я за столом»
      // може прийти раніше за відповідь на JoinRoom.
      autoStart = b.dataset.room;
      const r = await HGames.sitAt(b.dataset.room, b);
      if (!r || !r.ok) autoStart = null;
      return;
    }
    const label = b.textContent;
    b.disabled = true;
    try {
      if (k === 'own') {
        b.textContent = '⏳ Ставимо стіл…';
        const r = await HGames.call('CreateRoom', 'fura', { collide: wantMode(ctxNow && ctxNow.room, false) });
        if (r && r.ok && r.roomId) { autoStart = r.roomId; HGames.openAt(r.roomId); }
      }
    } finally { b.disabled = false; b.textContent = label; }
  }

  async function start(root, btn, mode) {
    if (game || starting) return;
    starting = true;
    const err = root.querySelector('.fura-err');
    const label = btn.textContent;
    root.querySelectorAll('.fura-go').forEach((b) => { b.disabled = true; });
    btn.textContent = '⏳ Заводимо…';
    if (err) err.textContent = '';
    try {
      const mod = await import('/games/fura/main.js');
      bridge.onView = null;
      bridge.onFrame = null;
      gameMode = mode;
      game = await mod.start({ onExit: stop, mode, server: mode === 'kray' ? bridge : null });
    } catch (e) {
      console.error('[fura]', e);
      if (err) err.textContent = 'Не завелось: ' + (e && e.message ? e.message : e);
    } finally {
      starting = false;
      root.querySelectorAll('.fura-go').forEach((b) => { b.disabled = false; });
      btn.textContent = label;
    }
  }

  function stop() {
    const g = game;
    game = null;
    bridge.onView = null;
    bridge.onFrame = null;
    if (g) { try { g.stop(); } catch (e) { console.warn('[fura] stop', e); } }
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
      ctxNow = ctx; want = null; card(root);   // новий стіл — перемикач знову показує його режим
      clearInterval(paintT);
      paintT = setInterval(() => { if (root.isConnected) paint(root); }, 2000);   // лобі міняється й без нашого виду
    },
    update(root, ctx) {
      ctxNow = ctx;
      if (game && bridge.onView) { try { bridge.onView(ctx.view); } catch (e) { console.warn('[fura] view', e); } }
      paint(root);
      // сіли за стіл (чи поставили свій) з картки — одразу в край
      if (!game && autoStart && ctx.room && ctx.room.id === autoStart && ctx.mine) {
        autoStart = null;
        const b = root.querySelector('button[data-do=kray]');
        if (b) start(root, b, 'kray');
      }
    },
    frame(root, ctx, f) {
      if (game && bridge.onFrame && f && Array.isArray(f.p)) { try { bridge.onFrame(f); } catch (e) { console.warn('[fura] frame', e); } }
    },
    unmount() { clearInterval(paintT); paintT = 0; stop(); },
    status() { return game ? (gameMode === 'kray' ? 'у рейсі' : 'на полігоні') : ''; },
  });
})();
