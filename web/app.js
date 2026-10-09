(() => {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmt = (sec) => { sec = Math.max(0, Math.floor(sec || 0)); const m = Math.floor(sec / 60), s = sec % 60; return `${m}:${String(s).padStart(2, '0')}`; };
  const tm = (iso) => new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
  const plural = (n, one, few, many) => `${n} ${n % 10 === 1 && n % 100 !== 11 ? one : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? few : many}`;
  const tracksN = (n) => plural(n, 'трек', 'треки', 'треків');
  const isMobile = () => window.matchMedia('(max-width: 900px)').matches;
  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  /// Нік, по якому можна клацнути: колір свій у кожного (web/people.js), клік — картка людини. badge — значок, куплений
  /// у Лавці, перед ніком: у балачках, де людей багато й хочеться впізнати одразу (data-nb — щоб перемалювати на льоту).
  /// shown — як нік написати, якщо не в називному: «від Олі» (відмінок дає HLavka.genitive), клікається все одно на «Оля».
  const nickHtml = (n, cls, badge, shown) => `<span class="${cls || 'n'} who-n${HPeople.nickCls(n)}" data-who="${esc(n)}"${badge ? ' data-nb="1"' : ''} style="--h:${HPeople.hue(n)}">${badge ? HPeople.badge(n) : ''}${esc(shown == null ? n : shown)}</span>`;
  /// Для «від Олі» замість «закинув Оля» (рід людини невідомий): родовий відмінок ніка. «хтось» — так сервер зве
  /// замовника, якого не пам'ятає після перезапуску, це не нік.
  const fromNick = (n) => (n === 'хтось' ? 'когось' : HLavka.genitive(n));
  // Голосове — такий самий трек у черзі, тільки з нашим id і без обкладинки: замість неї мікрофон.
  const isVoice = (t) => !!t && String(t.id || '').startsWith('voice-');
  const cover = (t, attrs) => (t && t.thumbUrl
    ? `<img src="${esc(t.thumbUrl)}" alt=""${attrs ? ' ' + attrs : ''}>`
    : `<div class="noimg${isVoice(t) ? ' voice' : ''}">${isVoice(t) ? '🎙' : ''}</div>`);
  const voiceBtn = (t) => (isVoice(t) ? `<button class="ghost vplay" data-id="${esc(t.id)}" title="Послухати">▶</button>` : '');
  const EMOJIS = ['🔥', '❤️', '😂', '🕺', '🤘', '😴', '🤮', '🫠'];
  // Смайли для чату. Перша купка — ті самі, що літають над обкладинкою, далі просто по темах.
  const EMOJI_GROUPS = [
    { name: 'Ті, що літають', list: EMOJIS },
    { name: 'Пики', list: ['😀', '😁', '😂', '🤣', '😊', '😉', '😍', '😘', '😜', '🤪', '🤨', '🧐', '😎', '🥳', '😏', '🤤', '😢', '😭', '😤', '😡', '🤯', '😱', '🥶', '🤢', '🤒', '🤠', '🥴', '🤔', '🤫', '🙄', '😬', '🫡', '🤗', '🥺', '😇', '🤡', '💀', '👻'] },
    { name: 'Руки', list: ['👍', '👎', '👌', '🤙', '✌️', '🤝', '👏', '🙌', '🙏', '💪', '🫶', '👋', '🤌', '🖖', '☝️', '🤞'] },
    { name: 'Музика', list: ['🎵', '🎶', '🎧', '🎤', '🎸', '🥁', '🎹', '🎺', '🎻', '📻', '💃', '🔊', '⚡', '✨', '🎉', '🎊'] },
    { name: 'Всяке', list: ['🇺🇦', '🧡', '💛', '💚', '💙', '💜', '🖤', '💔', '⭐', '🌟', '🍺', '🍻', '☕', '🍕', '🌻', '🌚', '🌞', '🐈', '🐕', '⚽', '🏆', '🚀', '💩', '🥔'] },
  ];
  // «Тільки смайли» — таке повідомлення показуємо великим. Крім самих значків пускаємо пробіли,
  // селектор емодзі (FE0F), склейку (200D), відтінки шкіри і пари літер прапора.
  const EMOJI_TEXT = /^(?:\p{Extended_Pictographic}|\p{Regional_Indicator}|[\u{1F3FB}-\u{1F3FF}\uFE0F\u200D\s])+$/u;
  const EMOJI_SEQ = /\p{Extended_Pictographic}(?:[\u{1F3FB}-\u{1F3FF}]|\uFE0F)*(?:\u200D\p{Extended_Pictographic}(?:[\u{1F3FB}-\u{1F3FF}]|\uFE0F)*)*|\p{Regional_Indicator}{2}/gu;
  /// Скільки смайлів у рядку, якщо в ньому взагалі нема нічого іншого; інакше 0.
  function emojiCount(text) {
    const t = String(text || '').trim();
    if (!t || !EMOJI_TEXT.test(t)) return 0;
    return (t.match(EMOJI_SEQ) || []).length;
  }

  // account — чи це акаунт із паролем; інакше нік гостьовий, з приставкою «гість », і його дає сервер.
  let me = { nick: localStorage.getItem('nick') || '', role: 'member', account: false };
  let state = null;
  let conn = null;
  let searchTimer = null;
  let lastQuery = '';
  let lastPlayId = null;
  let unread = 0;
  let chatTab = 'chat';
  let libTab = 'history';
  let queueDur = [];
  let route = 'efir';                                        // efir | lib | games | chat (телефон)
  let chatOpen = localStorage.getItem('chatOpen') !== '0';   // балачки типово відкриті
  let baseTitle = 'Глечики';

  const dj = () => state?.djName || 'Дядько Глек';
  const djGen = () => state?.djNameGen || 'Дядька Глека';

  // ---------- екранна клавіатура (телефон) ----------
  // Поки пишеш у полі — body.kbd: вкладки й міні-плеєр ховаються (на Android вони лягали над клавіатурою), а шторка
  // «💬 Стіл» стає над клавіатурою. --kb — скільки знизу закрила клавіатура (iOS стискає лише visualViewport, fixed-низ
  // лишався під нею), --vvh — видима висота. Android із interactive-widget=resizes-content стискає макет сам (--kb ≈ 0).
  (() => {
    const vv = window.visualViewport, de = document.documentElement;
    const field = (el) => !!el && (el.tagName === 'TEXTAREA' || el.isContentEditable || el.tagName === 'SELECT'
      || (el.tagName === 'INPUT' && !/^(checkbox|radio|range|button|submit|reset|file|color|image)$/i.test(el.type)));
    // Поле з фокусом може просто зникнути з DOM (раунд скінчився, гра перемалювала) — Chrome тоді не шле focusout,
    // і kbd залипав би з схованими вкладками. Тож поки kbd, стежимо за зникненням вузлів (лише поки пишеш — дешево).
    let gone = null;
    const upd = () => {
      const a = document.activeElement;
      const on = field(a) && a.isConnected && window.matchMedia('(pointer: coarse)').matches;
      if (document.body.classList.contains('kbd') !== on) document.body.classList.toggle('kbd', on);
      if (on && !gone && window.MutationObserver) {
        gone = new MutationObserver((ms) => {
          const el = document.activeElement;
          if (ms.some((m) => m.removedNodes.length) && !(field(el) && el.isConnected)) upd();
        });
        gone.observe(document.body, { childList: true, subtree: true });
      } else if (!on && gone) { gone.disconnect(); gone = null; }
      if (vv) {
        de.style.setProperty('--kb', Math.max(0, Math.round(window.innerHeight - vv.height - vv.offsetTop)) + 'px');
        de.style.setProperty('--vvh', Math.round(vv.height) + 'px');
      }
    };
    document.addEventListener('focusin', upd);
    document.addEventListener('focusout', () => setTimeout(upd, 0));
    window.addEventListener('hashchange', () => setTimeout(upd, 0));
    if (vv) { vv.addEventListener('resize', upd); vv.addEventListener('scroll', upd); }
  })();

  // ---------- toasts / busy buttons ----------
  // Стос тостів закривав поле гри на телефоні (морський бій: відповідь на кожен постріл), тож той самий текст, що ще
  // висить, не множимо — лише подовжуємо йому життя, а разом на екрані не більше трьох: найстаріший іде першим.
  const TOASTS_MAX = 3;
  function toast(text, kind) {
    const box = $('toasts');
    const cls = 'toast ' + (kind || '');
    const ttl = kind === 'err' ? 5000 : 3200;
    if (kind !== 'wait') {
      for (const t of box.children) {
        if (t._text !== text || t.className !== cls) continue;
        clearTimeout(t._timer);
        t._timer = setTimeout(() => t.remove(), ttl);
        return t;
      }
    }
    // Лічимо й виганяємо лише звичайні тости: «Зачекай…» зі спінером прибирає той, хто чекає, а заклик до столу
    // й ачівку (core.js кладе їх сам) дрібниця на кшталт «Мимо!» витісняти не має.
    const plain = [...box.children].filter((t) => t._text !== undefined && !t.classList.contains('wait'));
    while (plain.length >= TOASTS_MAX) plain.shift().remove();
    const el = document.createElement('div');
    el.className = cls;
    el.innerHTML = (kind === 'wait' ? '<span class="spin"></span>' : '') + esc(text);
    el._text = text;
    box.appendChild(el);
    el._timer = setTimeout(() => el.remove(), ttl);
    return el;
  }
  const ok = (r) => toast(r.message, 'ok');
  const fail = (e) => toast(e.message, 'err');

  // Shows the click landed: spinner in the button until the server answers.
  async function busy(btn, label, fn) {
    if (!btn || btn.disabled) return;
    const html = btn.innerHTML;
    btn.disabled = true;
    btn.classList.add('busy');
    btn.innerHTML = `<span class="spin"></span> ${esc(label)}`;
    try { return await fn(); }
    finally { if (btn.isConnected) { btn.disabled = false; btn.classList.remove('busy'); btn.innerHTML = html; } }
  }

  // ---------- api ----------
  async function api(method, path, body) {
    // Лише дивишся (ще не назвався) — дивитись можна все, а робити щось — спершу назвись.
    if (!me.nick && method !== 'GET' && !path.startsWith('/api/account/')) {
      askNick(true);
      throw new Error('Агов, спершу назвись — тоді й тисни');
    }
    const r = await fetch(path, {
      method,
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent(me.nick) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    let data = null;
    try { data = await r.json(); } catch { /* no body */ }
    if (!r.ok) throw Object.assign(new Error((data && data.message) || `HTTP ${r.status}`), { data });
    return data;
  }
  const queueTrack = (id) => api('POST', `/api/queue/track/${id}`).then(ok).catch(fail);

  // ---------- nick ----------
  // Одна картка на всі випадки: зайти (паролем чи через Google), зареєструвати нік, піти гостем; після
  // Google для новенького — «як тебе кликати?» (gnick); а для того, хто вже в акаунті — «Ти — Влад»:
  // вийти, змінити пароль (password), прив'язати Google. Нік каже сервер: акаунт — із куки, гість —
  // «гість » + те, що набрав. Після входу чи виходу сторінка перезавантажується: хаб тримає одне
  // з'єднання на вкладку і чужу куку на льоту не підхопить.
  const NICK_HINTS = {
    login: 'Нік і пароль. Забув пароль — зайди через Google, якщо прив\'язував, або попроси адміна (/пароль у балачках).',
    register: 'Нік стане твоїм: під ним ніхто інший не напише, а глеки й ачівки під цим іменем — твої.',
    guest: 'Без пароля. Будеш «гість Вася»: усе, що заробиш, лежатиме під цим іменем.',
    gnick: 'Google тебе підтвердив. Обери нік — під ним тебе тут знатимуть, пароль не потрібен.',
    me: 'Вийти — і ти знову гість. Глеки й ачівки лишаються за ніком, зайдеш — усе на місці.',
    password: 'Новий пароль — хоча б 6 символів. Інші вкладки з цим акаунтом доведеться перезайти.',
  };
  const NICK_BUTTONS = {
    login: 'Зайти', register: 'Зареєструватись', guest: 'Заходжу як гість', gnick: 'Заходжу',
    me: 'Вийти з акаунта', password: 'Зберегти пароль',
  };
  let nickMode = 'register';
  let pendingGoogle = null;   // ID-токен від Google, поки новенький обирає нік
  const guestBody = (n) => String(n || '').replace(/^гість\s*/i, '').trim();
  function setNickMode(mode) {
    nickMode = mode;
    const isMe = mode === 'me' || mode === 'password';
    $('nickTabs').querySelectorAll('button').forEach((b) => b.classList.toggle('on', b.dataset.mode === mode));
    $('nickTabs').hidden = isMe || mode === 'gnick';
    $('nickInput').hidden = isMe;
    $('passCurrent').hidden = mode !== 'password' || !me.hasPassword;
    $('passInput').hidden = mode === 'guest' || mode === 'gnick' || mode === 'me';
    $('passInput').autocomplete = mode === 'login' ? 'current-password' : 'new-password';
    $('passInput').placeholder = mode === 'password' ? 'новий пароль' : 'пароль';
    $('nickTitle').textContent = isMe ? `Ти — ${me.nick}` : 'Хто прийшов?';
    $('nickHint').textContent = NICK_HINTS[mode];
    $('nickSave').textContent = NICK_BUTTONS[mode];
    $('nickSave').classList.toggle('primary', mode !== 'me');
    $('nickPass').hidden = mode !== 'me';
    $('nickPass').textContent = me.hasPassword ? 'Змінити пароль' : 'Поставити пароль';
    $('nickErr').hidden = true;
    paintMeInfo();
    paintGoogle();
  }
  function paintMeInfo() {
    const box = $('meInfo');
    box.hidden = nickMode !== 'me';
    if (box.hidden) return;
    const lines = [];
    if (me.google) lines.push(`Google прив'язано${me.email ? ' · ' + esc(me.email) : ''}.`);
    else lines.push('Прив\'яжи Google — заходитимеш без пароля, і нік не пропаде, якщо його забудеш.');
    if (!me.hasPassword) lines.push('Пароля нема: заходиш через Google. Хочеш — постав.');
    box.innerHTML = lines.map((l) => `<div class="muted">${l}</div>`).join('');
  }
  function askNick(force, mode, prefill) {
    if (me.nick && !force) return;
    // Новенькому — найпростіше: гостем, одним полем. Акаунт і пароль — поруч, для тих, кому треба.
    setNickMode(mode || (me.account ? 'me' : me.nick ? 'login' : 'guest'));
    $('nickInput').value = prefill !== undefined ? prefill : guestBody(me.nick);
    $('passInput').value = '';
    $('passCurrent').value = '';
    // Без ніка сайт однаково видно — лише подивитись, тож і картку можна закрити: вона не стіна.
    $('nickLater').hidden = false;
    $('nickLater').textContent = me.nick ? 'Не зараз' : 'Лише подивлюсь';
    $('nickModal').hidden = false;
    if (!$('nickInput').hidden) setTimeout(() => $('nickInput').focus(), 50);
    else if (nickMode === 'password') setTimeout(() => ($('passCurrent').hidden ? $('passInput') : $('passCurrent')).focus(), 50);
  }
  function paintNick() {
    const b = $('meBtn');
    $('nickBtn').textContent = me.nick || 'назватись';
    $('meAva').outerHTML = me.nick ? HPeople.ava(me.nick, 'ava', 'meAva') : '<span id="meAva" class="ava none" aria-hidden="true">?</span>';
    b.classList.toggle('admin', me.role === 'admin');
    b.classList.toggle('guest', !me.account);
    b.href = me.nick ? '#who/' + encodeURIComponent(me.nick) : '#who';
    b.title = !me.nick ? 'Назватись, щоб тяпати й грати'
      : me.account ? 'Твій профіль: черепки, ачівки, акаунт' : 'Твій профіль. Гість — зареєструй нік, щоб він був лише твій';
  }
  function showNickError(text) { $('nickErr').textContent = text; $('nickErr').hidden = false; }
  async function saveNick() {
    const n = $('nickInput').value.trim().slice(0, 24);
    const password = $('passInput').value;
    $('nickErr').hidden = true;
    try {
      if (nickMode === 'me') {
        await api('POST', '/api/account/logout');
        localStorage.removeItem('nick');
        location.reload();
        return;
      }
      if (nickMode === 'password') {
        if (password.length < 6) { showNickError('Пароль — хоча б 6 символів'); return; }
        await busy($('nickSave'), 'Зберігаю…', () => api('POST', '/api/account/password', { current: $('passCurrent').value, password }));
        me.hasPassword = true;
        $('nickModal').hidden = true;
        toast('Є! Пароль збережено', 'ok');
        return;
      }
      if (!n) return;
      if (nickMode === 'guest') { await becomeGuest(n); return; }
      if (nickMode === 'gnick') {
        const r = await busy($('nickSave'), 'Заходжу…', () => api('POST', '/api/account/google', { credential: pendingGoogle, nick: n }));
        if (!r.ok) { showNickError(r.message || 'Халепа: не вийшло'); return; }
        localStorage.setItem('nick', r.nick);
        location.reload();
        return;
      }
      if (password.length < 6) { showNickError('Пароль — хоча б 6 символів'); return; }
      const r = await busy($('nickSave'), nickMode === 'login' ? 'Заходжу…' : 'Реєструю…',
        () => api('POST', `/api/account/${nickMode}`, { nick: n, password }));
      localStorage.setItem('nick', r.nick);
      location.reload();
    } catch (e) { showNickError(e.message); }
  }
  // Гостьовий нік остаточно складає сервер (приставка, довжина): питаємо /api/me з новим X-Nick.
  async function becomeGuest(n) {
    const was = me.nick;
    me.nick = n;
    let m;
    try { m = await api('GET', '/api/me'); }
    catch (e) { me.nick = was; throw e; }
    me.nick = m.nick;
    localStorage.setItem('nick', m.nick);
    $('nickModal').hidden = true;
    paintNick();
    if (m.nick !== was && conn && conn.state === 'Connected') conn.invoke('SetNick', m.nick).catch(() => {});
    if (!conn) connect();
    else render();
  }
  $('nickForm').onsubmit = (e) => { e.preventDefault(); saveNick(); };
  $('nickTabs').querySelectorAll('button').forEach((b) => b.onclick = () => { setNickMode(b.dataset.mode); $('nickInput').focus(); });
  $('nickPass').onclick = () => askNick(true, 'password');
  $('nickLater').onclick = () => { $('nickModal').hidden = true; };
  // Без ніка профільний чип — це «назватись», а не сторінка порожнього профілю.
  $('meBtn').addEventListener('click', (e) => { if (!me.nick) { e.preventDefault(); askNick(true); } });

  // ---------- вхід через Google ----------
  // Бібліотеку Google тягнемо лише коли сервер дав Client ID; вона сама малює кнопку в наш контейнер
  // і віддає підписаний ID-токен — його перевіряє сервер. Та сама кнопка в картці «Ти — …» — прив'язка.
  let googleReady = false;
  function loadGoogle(clientId) {
    if (!clientId || googleReady || document.getElementById('gsi')) return;
    const s = document.createElement('script');
    s.id = 'gsi';
    s.src = 'https://accounts.google.com/gsi/client';
    s.async = true;
    s.onload = () => {
      try {
        google.accounts.id.initialize({ client_id: clientId, callback: onGoogle, ux_mode: 'popup', itp_support: true });
        googleReady = true;
        if (!$('nickModal').hidden) paintGoogle();
      } catch (e) { console.warn('[google]', e); }
    };
    document.head.appendChild(s);
  }
  function paintGoogle() {
    const box = $('googleBox');
    const want = googleReady && (nickMode === 'login' || nickMode === 'register' || (nickMode === 'me' && !me.google));
    box.hidden = !want;
    if (!want) return;
    const el = $('googleBtn');
    el.innerHTML = '';
    const width = Math.max(200, Math.min(400, el.parentElement.clientWidth || 300));
    google.accounts.id.renderButton(el, {
      // Світла тема: персональний варіант кнопки («Увійти як Коля») Google малює на білому блоці, і на
      // темній картці чорна пігулка в білій рамці виглядала як помилка. Біла пігулка на білому — рівна.
      theme: 'outline', size: 'large', shape: 'pill', locale: 'uk', width,
      text: nickMode === 'me' ? 'continue_with' : 'signin_with',
    });
  }
  async function onGoogle(resp) {
    const credential = resp && resp.credential;
    if (!credential) return;
    $('nickErr').hidden = true;
    try {
      if (me.account) {
        await api('POST', '/api/account/google/link', { credential });
        toast('Файно! Google прив\'язано', 'ok');
        location.reload();
        return;
      }
      const r = await api('POST', '/api/account/google', { credential });
      if (r.needNick) { pendingGoogle = credential; askNick(true, 'gnick', r.suggest || ''); return; }
      if (!r.ok) { showNickError(r.message || 'Халепа: не вийшло'); return; }
      localStorage.setItem('nick', r.nick);
      location.reload();
    } catch (e) { showNickError(e.message); }
  }

  // ---------- player ----------
  const audio = $('audio');
  const vol = $('volume');
  // повзунок 0..100 іде по децибелах, а не лінійно: крок = 0.5 дБ, 1 → −50 дБ, 100 → 0 дБ.
  // Так тихі рівні мають десятки кроків замість двох-трьох. У localStorage лежить сама гучність 0..1.
  const VOL_DB = 50;
  const posToVol = (p) => p <= 0 ? 0 : Math.pow(10, -VOL_DB * (1 - p / 100) / 20);
  const volToPos = (v) => v <= 0 ? 0 : Math.min(100, Math.max(1, Math.round(100 * (1 + 20 * Math.log10(v) / VOL_DB))));
  // Посиденьки (web/voice.js) притишують радіо, коли хтось говорить: множник поверх гучності з повзунка.
  let duckBy = 1;
  // Гімн переможця притишує радіо своїм множником (до 20 %): Посиденьки й гімн не перетирають одне одному duckBy.
  let duckAnthem = 1;
  let anthemEl = null;            // один Audio на весь сайт — для гімнів за столом і прослуховування в Лавці
  let anth = null;                // що звучить зараз: { a, preview, onEnd, timer } або null
  function setVolPos(p, save) {
    p = Math.min(100, Math.max(0, p));
    vol.value = p;
    const v = posToVol(p);
    audio.volume = v * duckBy * duckAnthem;
    // гімн іде тією ж гучністю, що й радіо: крутнули повзунок — і він за ним
    if (anth && anthemEl) { try { anthemEl.volume = v; } catch { /* iOS: лише читання */ } anthemEl.muted = p === 0; }
    vol.title = p ? `Гучність ${p} (${(20 * Math.log10(v)).toFixed(1)} дБ) · колесо миші — по кроку` : 'Гучність: тиша';
    if (save) localStorage.setItem('volume', String(v));
  }
  const savedVol = parseFloat(localStorage.getItem('volume') ?? '');
  setVolPos(Number.isFinite(savedVol) ? volToPos(savedVol) : 90, false);
  vol.oninput = () => setVolPos(parseInt(vol.value, 10), true);
  // клац колеса = 1 крок; тачпад шле дрібні дельти, тож накопичуємо їх, щоб гучність не злітала
  let wheelAcc = 0;
  vol.addEventListener('wheel', (e) => {
    e.preventDefault();
    if (e.deltaMode !== 0) wheelAcc = -Math.sign(e.deltaY) * 100;
    else wheelAcc -= e.deltaY;
    const steps = Math.trunc(wheelAcc / 100);
    if (!steps) return;
    wheelAcc -= steps * 100;
    setVolPos(parseInt(vol.value, 10) + steps, true);
  }, { passive: false });
  // На вужчому екрані повзунок ховається за 🔊 і випадає під ним — шапка не лізе у два рядки.
  const volWrap = $('volWrap');
  const setVolOpen = (on) => { volWrap.classList.toggle('open', on); $('volBtn').setAttribute('aria-expanded', String(on)); };
  $('volBtn').onclick = (e) => { e.stopPropagation(); setVolOpen(!volWrap.classList.contains('open')); };
  document.addEventListener('click', (e) => { if (volWrap.classList.contains('open') && !e.target.closest('#volWrap')) setVolOpen(false); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') setVolOpen(false); });
  const paintVolIcon = () => { const p = +vol.value; $('volBtn').textContent = p === 0 ? '🔇' : p < 45 ? '🔉' : '🔊'; };
  vol.addEventListener('input', paintVolIcon);
  vol.addEventListener('wheel', paintVolIcon);
  paintVolIcon();
  let playState = 'idle'; // idle | connecting | live
  function setPlayUi() {
    const b = $('playBtn');
    if (playState === 'idle') { b.className = 'primary'; b.textContent = '▶ Врубити'; b.title = 'Слухати ефір прямо тут'; }
    else if (playState === 'connecting') { b.className = 'primary busy'; b.innerHTML = '<span class="spin"></span> Врубаю…'; }
    else { b.className = 'live'; b.innerHTML = '<span class="dot"></span> В ефірі · Вирубити'; b.title = 'Вирубити звук'; }
  }
  // сервер записує, хто слухав кожен трек (вкладка «Рейтинг»); ETS2 і VLC він бачить лише числом у потоці
  let listening = false;
  function tellListening(on) {
    if (listening === on) return;
    listening = on;
    if (conn && conn.state === 'Connected') conn.invoke('SetListening', on).catch(() => {});
  }
  function stopAudio() {
    tellListening(false);
    audio.pause();
    audio.removeAttribute('src');
    audio.load();
    playState = 'idle';
    setPlayUi();
  }
  // auto — після «Оновити» на плашці (resumeAudio): жесту на цій сторінці ще не було, і браузер може не дати звуку.
  async function startAudio(auto) {
    const url = state?.streamUrl || '';
    if (!url) { toast('Халепа: адреса потоку не налаштована', 'err'); return; }
    playState = 'connecting';
    setPlayUi();
    audio.src = url + (url.includes('?') ? '&' : '?') + '_=' + Date.now();
    try { await audio.play(); }
    catch (e) {
      if (auto) { stopAudio(); toast('Сайт оновлено. Сам звук браузер не врубив — тисни «▶ Врубити»'); return; }
      playState = 'idle'; setPlayUi(); toast('Халепа: потік не врубився — ' + e.message, 'err');
    }
  }
  $('playBtn').onclick = () => { if (playState !== 'idle') stopAudio(); else startAudio(false); };
  audio.addEventListener('playing', () => { playState = 'live'; setPlayUi(); updateMediaSession(); tellListening(true); });
  audio.addEventListener('pause', () => tellListening(false));
  audio.addEventListener('waiting', () => { if (playState === 'live') { playState = 'connecting'; setPlayUi(); } });
  audio.addEventListener('error', () => { if (playState !== 'idle') { stopAudio(); toast('Ой-йой, потік обірвався. Натисни «Врубити» ще раз', 'err'); } });
  audio.addEventListener('ended', () => { if (playState !== 'idle') { stopAudio(); toast('Ой-йой, потік закінчився. Натисни «Врубити» ще раз', 'err'); } });
  /// В ефірі справжній трек: замовлення, вибір Глека чи трек запаски (нове не вантажиться — грає знайоме з кешу).
  const trackOnAir = (n) => n.source === 'user' || n.source === 'autodj' || n.source === 'spare';
  function updateMediaSession() {
    if (!('mediaSession' in navigator) || !state) return;
    const n = state.now, t = n.track;
    const playingTrack = t && trackOnAir(n);
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: playingTrack ? t.title : 'Тиша',
        artist: playingTrack ? t.artist : state.siteName,
        album: state.siteName,
        artwork: playingTrack && t.thumbUrl ? [{ src: t.thumbUrl, sizes: '512x512', type: 'image/jpeg' }] : [],
      });
      navigator.mediaSession.setActionHandler('pause', () => stopAudio());
      navigator.mediaSession.setActionHandler('stop', () => stopAudio());
      // «наступний трек» на навушниках і клавіатурі — це наш скіп: ефір один на всіх
      navigator.mediaSession.setActionHandler('nexttrack', () => skipNow());
    } catch { /* unsupported */ }
  }

  // ---------- now playing ----------
  function nowRemaining() {
    if (!state) return 0;
    const n = state.now;
    if (!trackOnAir(n)) return 0;
    const elapsed = (Date.now() - new Date(n.startedAt).getTime()) / 1000 - (state.streamDelaySeconds || 0);
    return Math.max(0, (n.durationSec || 0) - elapsed);
  }
  function etaText(sec) {
    if (sec < 25) return 'ось-ось';
    if (sec < 75) return 'десь за хвилину';
    return `за ~${Math.round(sec / 60)} хв`;
  }

  let nowSig = '', queueSig = '', sugSig = '';
  let dragging = null, pendingQueueRender = false; // queue drag-to-reorder state

  const reactsHtml = () => EMOJIS.map((e) => `<button data-e="${e}">${e}</button>`).join('');
  /// Феєрверк — вміння з Лавки: хто купив, у того поруч із реакціями ще й 🎆.
  const fwHtml = () => (HLavka.perk('fireworks')?.owned
    ? '<button type="button" class="fwbtn" title="Бахнути феєрверк над обкладинкою в усіх — твоє вміння з Лавки, раз на 10 хвилин">🎆</button>' : '');
  function wireReacts(box) {
    box.querySelectorAll('.reacts button').forEach((b) => b.onclick = () => {
      if (conn) conn.invoke('React', b.dataset.e).catch(() => {});
    });
    box.querySelector('.fwbtn')?.addEventListener('click', (e) => launchFireworks(e.currentTarget));
  }
  const skipNow = () => api('POST', '/api/skip').then(ok).catch(fail);
  $('hdrSkip').onclick = (e) => busy(e.currentTarget, '', skipNow);

  /// Лайк, скіп, плейлист і бан — один набір обробників на обидві копії трека (панель і шапка),
  /// щоб не тримати дві однакові гілки, які розійдуться від першої ж правки.
  function wireNow(box, t, o) {
    const at = (act) => box.querySelector(`[data-act="${act}"]`);
    at('like')?.addEventListener('click', (e) => busy(e.currentTarget, '', () => api('POST', `/api/like/${t.id}`).catch(fail)));
    at('skip')?.addEventListener('click', (e) => busy(e.currentTarget, o.mini ? '' : 'скіп…', skipNow));
    at('pl')?.addEventListener('click', () => openPlaylistPicker(t.id, t.title));
    at('ban')?.addEventListener('click', (e) => {
      const banPrice = me.role === 'admin' ? 0 : (me.banPrice || 0);
      const ask = banPrice
        ? `Забанити «${t.title}» назавжди за ${banPrice} 🏺?
Трек скіпнеться і більше не заграє. Викупити його з бану теж коштуватиме черепки.`
        : 'Забанити цей трек назавжди?';
      if (!confirm(ask)) return;
      busy(e.currentTarget, 'баню…', () => api('POST', `/api/ban/${t.id}`).then((r) => { ok(r); if (route === 'lib' && libTab === 'bans') loadLib(); }).catch(fail));
    });
  }

  /// Те, що в ефірі, малюється двічі з одного джерела: велика панель «В ефірі» (o.mini не задано)
  /// і міні-плеєр у шапці (o.mini). Шапка бере коротку версію — обкладинка, назва, ❤ і ⏭.
  function paintNowInto(box, o) {
    const n = state.now;
    const live = trackOnAir(n);
    const mini = !!o.mini;
    if (!live) {
      const title = 'Тиша';
      const sub = `${dj()} думає, що б його врубити…`;
      box.innerHTML = mini
        ? `<a class="mini-cv" href="#efir" title="Перейти в Ефір"><img src="/static/glek.svg" alt=""></a>
           <a class="mini-tt" href="#efir" title="${esc(title)}"><b>${esc(title)}</b><small>${esc(sub)}</small></a>`
        : `<div class="coverwrap"><img class="cover dj" src="/static/glek.svg" alt=""></div>
        <div class="nowinfo">
          <div class="title">${esc(title)}</div>
          <div class="artist"><span class="art">${esc(sub)}</span></div>
          <div class="why">закинь щось або зачекай</div>
          <div class="actions"><span class="reacts">${reactsHtml()}</span>${fwHtml()}</div>
        </div>`;
      if (!mini) wireReacts(box);
      return;
    }
    const t = n.track || {};
    const liked = n.likers.some((x) => sameNick(x, me.nick));
    const pending = n.skipPending;
    const like = `<button data-act="like" class="${liked ? 'active' : ''}" title="${esc(n.likers.join(', ') || 'Вподобати')}">❤ ${n.likers.length}</button>`;
    const skip = `<button data-act="skip" ${pending ? 'disabled' : ''} title="Перемкнути на наступний трек">⏭${mini ? '' : '<span class="lbl"> Скіп</span>'}</button>`;
    if (mini) {
      box.innerHTML = `<a class="mini-cv" href="#efir" title="Перейти в Ефір">${cover(t)}</a>
        <a class="mini-tt" href="#efir" title="${esc(`${t.title} — ${t.artist}`)}"><b>${esc(t.title)}</b><small>${esc(t.artist)}</small></a>
        <span class="mini-acts">${like}${skip}</span>`;
      wireNow(box, t, o);
      return;
    }
    const by = n.source === 'user'
      ? `від ${nickHtml(n.requestedBy, 'bynick', false, fromNick(n.requestedBy))}${n.via === 'suggestion' ? ` <span class="chip dj">порада ${esc(djGen())}</span>` : ''}`
      : n.source === 'spare'
        ? `<b>${esc(dj())}</b> <span class="chip dj" title="Нове зараз не вантажиться, тож грає вже знайоме з полиці">запаска</span>`
        : `<b>${esc(dj())}</b> <span class="chip dj">авто</span>`;
    // адмін банить безкоштовно; решта — за черепки, і голосові не банять
    const banPrice = me.role === 'admin' ? 0 : (me.banPrice || 0);
    const canBan = me.role === 'admin' || (banPrice > 0 && !isVoice(t));
    // Щільно, щоб на ноутбуці під панеллю одразу було видно чергу й поради: хто закинув — у рядку з виконавцем,
    // час — по краях смужки, реакції — у тому ж рядку, що й кнопки (на вузькому самі перейдуть нижче).
    box.innerHTML = `
      <div class="coverwrap">${t.thumbUrl ? `<img class="cover" src="${esc(t.thumbUrl)}" alt="">` : `<div class="cover placeholder">${isVoice(t) ? '🎙' : '♪'}</div>`}</div>
      <div class="nowinfo">
        <div class="title">${esc(t.title)}</div>
        <div class="artist"><span class="art">${esc(t.artist)}</span><span class="by">${by}</span></div>
        ${n.reason ? `<div class="why">${esc(n.reason)}</div>` : ''}
        <div class="progline"><span id="tElapsed">0:00</span><div class="progress ${pending ? 'pending' : ''}"><div id="bar"></div></div><span>${fmt(n.durationSec)}</span></div>
        ${pending ? `<div class="pending-note"><span class="spin"></span> Перемикаю, в ефірі зміниться за кілька секунд</div>` : ''}
        <div class="actions">
          ${like}${skip}
          <button data-act="pl" title="Зберегти в плейлист">📂＋</button>
          ${t.sourceUrl ? `<a class="chip src" href="${esc(t.sourceUrl)}" target="_blank" rel="noopener" title="${isVoice(t) ? 'Послухати голосове' : 'Відкрити джерело'}">↗</a>` : ''}
          ${canBan ? `<button data-act="ban" class="danger ghost" title="${banPrice ? `Забанити назавжди за ${banPrice} черепків: трек скіпнеться і більше не заграє` : 'Забанити трек і скіпнути'}">🚫${banPrice ? ` ${banPrice} 🏺` : ' бан'}</button>` : ''}
          <span class="reacts" title="Реакція — полетить над обкладинкою в усіх">${reactsHtml()}</span>${fwHtml()}
        </div>
      </div>`;
    wireNow(box, t, o);
    wireReacts(box);
  }

  function renderNow() {
    const n = state.now;
    const sig = JSON.stringify([n.playId, n.itemId, n.source, n.track?.id, n.likers, n.skipPending, n.requestedBy, n.via, n.reason,
      n.durationSec, n.startedAt, state.liquidsoapOk, state.listeners, me.role, me.nick, me.banPrice, state.siteName, state.djName,
      !!HLavka.perk('fireworks')?.owned]);
    if (sig === nowSig) return;
    nowSig = sig;
    const banner = $('banner');
    banner.hidden = state.liquidsoapOk;
    banner.textContent = 'Ой-йой: ефір не відповідає (liquidsoap). Черга збережеться, треки підуть, щойно він оживе.';
    // Зелений чіп «ефір» у шапці — шум: показуємо лише тоді, коли з ефіром щось не так.
    $('liqStatus').hidden = !!state.liquidsoapOk;
    $('liqStatus').className = 'chip err';
    $('liqStatus').textContent = 'ефір ↓';

    paintNowInto($('now'), {});
    paintNowInto($('nowMini'), { mini: true });

    const playingTrack = n.track && trackOnAir(n);
    // ⏭ у шапці (видно лише на телефоні за столом і в балачках — це вирішує CSS): є що скіпати — є й кнопка.
    $('hdrSkip').hidden = !playingTrack;
    $('hdrSkip').disabled = !!n.skipPending;
    baseTitle = playingTrack ? `${n.track.title} — ${n.track.artist} · ${state.siteName}` : state.siteName;
    paintTitle();
    if (playState !== 'idle') updateMediaSession();
    tick();
  }

  function tick() {
    if (!state) return;
    const n = state.now;
    const live = trackOnAir(n);
    const d = live ? (n.durationSec || 0) : 0;
    let elapsed = 0, pct = 0;
    if (live) {
      const raw = (Date.now() - new Date(n.startedAt).getTime()) / 1000 - (state.streamDelaySeconds || 0);
      elapsed = Math.max(0, Math.min(raw, d || raw));
      pct = d ? Math.min(100, (elapsed / d) * 100) : 0;
    }
    const bar = $('bar'), el = $('tElapsed');
    if (bar && el) { el.textContent = fmt(elapsed); bar.style.width = d ? pct + '%' : '0%'; }
    // Смужка під шапкою: скільки лишилось треку видно з будь-якого розділу.
    $('hdrBar').style.width = d ? pct + '%' : '0%';
    $('hdrProg').classList.toggle('pending', !!n.skipPending);
    // ETAs in the queue: what is left of the current track plus everything queued before the item
    let acc = nowRemaining();
    document.querySelectorAll('[data-eta]').forEach((s) => {
      const i = +s.dataset.eta;
      let sum = acc;
      for (let j = 0; j < i; j++) sum += queueDur[j] || 0;
      s.textContent = etaText(sum);
    });
    const an = document.querySelector('[data-eta-after]');
    if (an) an.textContent = etaText(acc + queueDur.reduce((a, b) => a + b, 0));
  }
  setInterval(tick, 1000);

  function flyEmoji(emoji, nick) {
    const layer = $('flyLayer');
    const cover = document.querySelector('#now .cover');
    const pr = layer.parentElement.getBoundingClientRect();
    const cr = cover ? cover.getBoundingClientRect() : pr;
    const el = document.createElement('div');
    el.className = 'fly';
    el.style.left = (cr.left - pr.left + cr.width * (0.3 + Math.random() * 0.4)) + 'px';
    el.style.top = (cr.top - pr.top + cr.height * 0.75) + 'px';
    el.innerHTML = `${esc(emoji)}<small>${esc(nick)}</small>`;
    layer.appendChild(el);
    setTimeout(() => el.remove(), 2500);
  }

  // ---------- 🎆 феєрверк і 💌 присвята (вміння з Лавки Дядька Глека, web/lavka.js) ----------
  function launchFireworks(btn) {
    if (!conn) return;
    const p = HLavka.perk('fireworks');
    if (p && p.readyAt && Date.parse(p.readyAt) > Date.now()) { toast('Феєрверк ще заряджається: ' + HLavka.readyIn(p.readyAt), 'err'); return; }
    busy(btn, '', () => conn.invoke('Fireworks').then((err) => {
      if (err) toast(err, 'err'); else HLavka.usedPerk('fireworks', 10);
    }).catch(fail));
  }
  /// Три спалахи над обкладинкою, кожен — кільце іскор свого кольору, і підпис, хто запустив.
  function fireworks(nick) {
    const layer = $('flyLayer');
    if (!layer || document.hidden) return;
    const cover = document.querySelector('#now .cover');
    const pr = layer.parentElement.getBoundingClientRect();
    const cr = cover ? cover.getBoundingClientRect() : pr;
    for (let b = 0; b < 3; b++) {
      const burst = document.createElement('div');
      burst.className = 'fw';
      burst.style.left = (cr.left - pr.left + cr.width * (0.2 + Math.random() * 0.6)) + 'px';
      burst.style.top = (cr.top - pr.top + cr.height * (0.15 + Math.random() * 0.45)) + 'px';
      const hue0 = Math.floor(Math.random() * 360);
      const delay = b * 320;
      let html = '';
      for (let i = 0; i < 16; i++) {
        const a = (i / 16) * Math.PI * 2 + Math.random() * 0.2;
        const r = 45 + Math.random() * 35;
        html += `<i style="--dx:${(Math.cos(a) * r).toFixed(1)}px;--dy:${(Math.sin(a) * r).toFixed(1)}px;--c:hsl(${(hue0 + i * 9) % 360} 95% 66%);animation-delay:${delay}ms"></i>`;
      }
      burst.innerHTML = html;
      layer.appendChild(burst);
      setTimeout(() => burst.remove(), 1700 + delay);
    }
    const tag = document.createElement('div');
    tag.className = 'fly';
    tag.style.left = (cr.left - pr.left + cr.width * 0.5 - 20) + 'px';
    tag.style.top = (cr.top - pr.top + cr.height * 0.7) + 'px';
    tag.innerHTML = `🎆<small>${esc(nick || '')}</small>`;
    layer.appendChild(tag);
    setTimeout(() => tag.remove(), 2500);
  }

  // ---------- 🎉 святкування перемоги (docs/games/specs/flair.md §3) ----------
  // Шар поверх картки (стіл переможця; у Лавці — картка речі після ▶): pointer-events: none — ходити й тиснути не
  // заважає. Без бібліотек: частинки — <i> з CSS-анімацією, усе своє — у змінних style (--fh — висота картки). Вузлів —
  // не більше 80; після кінця шар зникає цілком. prefers-reduced-motion — замість руху нерухомий значок на ~2 с.
  const FX_EMOJI = { confetti: '🎊', shards: '🏺', sunflowers: '🌻', salute: '🎆', hopak: '💃', glekhopak: '💃' };
  const FX_MS = 6000;             // без гімну
  const FX_MAX_MS = 15000;        // і не довше за це, хоч який довгий гімн
  const rnd = (a, b) => a + Math.random() * (b - a);
  const FX_DRAW = {
    /// 🎊 різнокольорові смужки сиплються згори, крутяться й гойдаються
    confetti(layer) {
      let html = '';
      for (let i = 0; i < 56; i++) {
        const d = rnd(2.2, 3.8);
        html += `<i style="left:${rnd(0, 98).toFixed(1)}%;--c:hsl(${Math.floor(rnd(0, 360))} 90% 62%);--d:${d.toFixed(2)}s;--dl:${rnd(0, 2.4).toFixed(2)}s;`
          + `--sx:${rnd(-40, 40).toFixed(0)}px;--r:${rnd(-720, 720).toFixed(0)}deg;width:${rnd(5, 9).toFixed(0)}px;height:${rnd(9, 14).toFixed(0)}px"></i>`;
      }
      layer.innerHTML = html;
    },
    /// 🏺 глечики й глиняні черепки падають, б'ються об низ картки й підстрибують
    shards(layer) {
      let html = '';
      for (let i = 0; i < 22; i++) {
        const s = rnd(16, 30).toFixed(0);
        const pos = `left:${rnd(2, 92).toFixed(1)}%;--s:${s}px;--d:${rnd(2.4, 3.4).toFixed(2)}s;--dl:${rnd(0, 2.6).toFixed(2)}s;`
          + `--sx:${rnd(-50, 50).toFixed(0)}px;--r:${rnd(-200, 200).toFixed(0)}deg;--bh:${rnd(18, 46).toFixed(0)}px`;
        html += i % 3 === 0 ? `<i class="gl" style="${pos}">🏺</i>`
          : `<i class="sh" style="${pos};clip-path:polygon(${rnd(0, 30).toFixed(0)}% 0,100% ${rnd(0, 40).toFixed(0)}%,${rnd(60, 100).toFixed(0)}% 100%,0 ${rnd(50, 90).toFixed(0)}%)"></i>`;
      }
      layer.innerHTML = html;
    },
    /// 🌻 пелюстки й соняшники кружляють, гойдаючись, поки падають
    sunflowers(layer) {
      let html = '';
      for (let i = 0; i < 36; i++) {
        const sf = i % 4 === 0;
        html += `<i class="${sf ? 'sf' : 'pt'}" style="left:${rnd(0, 94).toFixed(1)}%;--d:${rnd(3.2, 5).toFixed(2)}s;--dl:${rnd(0, 3).toFixed(2)}s;`
          + `--sx:${rnd(20, 60).toFixed(0) * (Math.random() < 0.5 ? -1 : 1)}px;--r:${(Math.random() < 0.5 ? -1 : 1) * Math.floor(rnd(300, 600))}deg${sf ? ';--s:' + rnd(18, 30).toFixed(0) + 'px' : ''}">${sf ? '🌻' : ''}</i>`;
      }
      layer.innerHTML = html;
    },
    /// 🎆 той самий спалах, що й у вміння «Феєрверк» (.fw), лише над столом і раз за разом, доки триває
    salute(layer, w, h, timers) {
      const R = Math.min(w, h);
      const burst = () => {
        const b = document.createElement('div');
        b.className = 'fw';
        b.style.left = (w * rnd(0.15, 0.85)).toFixed(0) + 'px';
        b.style.top = (h * rnd(0.12, 0.55)).toFixed(0) + 'px';
        const hue0 = Math.floor(rnd(0, 360));
        let html = '';
        for (let i = 0; i < 14; i++) {
          const a = (i / 14) * Math.PI * 2 + rnd(0, 0.2);
          const r = R * rnd(0.14, 0.24) + 20;
          html += `<i style="--dx:${(Math.cos(a) * r).toFixed(1)}px;--dy:${(Math.sin(a) * r).toFixed(1)}px;--c:hsl(${(hue0 + i * 9) % 360} 95% 66%)"></i>`;
        }
        b.innerHTML = html;
        layer.appendChild(b);
        setTimeout(() => b.remove(), 1400);
      };
      burst();
      timers.push(setInterval(burst, 420));   // живих разом — до чотирьох спалахів по 14 іскор
    },
    /// 💃 Дядько Глек вистрибує знизу на стіл і танцює гопак: присядка, вибрик то лівою, то правою; над ним ноти
    hopak(layer, w, h) {
      const gs = Math.round(Math.min(140, Math.max(56, Math.min(w, h) * 0.32)));
      layer.style.setProperty('--gs', gs + 'px');
      layer.innerHTML = '<div class="fx-glek"><div class="fx-dance"><b class="fx-leg l"></b><b class="fx-leg r"></b>'
        + '<img src="/static/glek.svg" alt=""></div></div>'
        + ['🎶', '🎵', '🎶'].map((n, i) => `<i class="fx-note" style="--dl:${(0.9 + i * 0.55).toFixed(2)}s;--sx:${(i - 1) * gs * 0.5}px">${n}</i>`).join('');
    },
  };
  /// host — картка; id — святкування; opt.ms — скільки триває (типово 6 с, не довше 15). Вертає stop(): шар м'яко гасне.
  function playFx(host, id, opt) {
    if (!host || !FX_EMOJI[id]) return () => {};
    opt = opt || {};
    const old = host.querySelector(':scope > .gfx');
    if (old) { if (old._stop) old._stop(true); else old.remove(); }
    host.classList.add('gfx-on');
    const w = host.clientWidth || 300;
    const h = host.clientHeight || 300;
    const layer = document.createElement('div');
    // у каталозі гопак — glekhopak (id hopak уже в гімна), малюється тим самим
    layer.className = 'gfx fx-' + (FX_DRAW[id] ? id : 'hopak');
    layer.setAttribute('aria-hidden', 'true');
    layer.style.setProperty('--fh', h + 'px');
    host.appendChild(layer);
    const timers = [];
    let over = false;
    const stop = (now) => {
      if (over) return;
      over = true;
      timers.forEach((t) => { clearTimeout(t); clearInterval(t); });
      if (now === true) { layer.remove(); return; }
      layer.classList.add('out');
      setTimeout(() => layer.remove(), 450);
    };
    layer._stop = stop;
    let ms = Math.min(FX_MAX_MS, Math.max(1000, +opt.ms || FX_MS));
    const still = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (still) {
      layer.innerHTML = '<span class="fx-badge">' + FX_EMOJI[id] + '</span>';
      ms = Math.min(ms, 2000);
    } else {
      try { (FX_DRAW[id] || FX_DRAW.hopak)(layer, w, h, timers); } catch (e) { console.warn('[fx]', e); }
    }
    timers.push(setTimeout(() => stop(), ms));
    return () => stop();
  }

  /// Кому й як присвятити — віконце з кнопок (без вільного тексту: Глек читає це вголос на всіх).
  function openDedication(it) {
    if (!it) return;
    const p = HLavka.perk('dedication');
    if (p && p.readyAt && Date.parse(p.readyAt) > Date.now()) { toast('Присвята ще відпочиває: ' + HLavka.readyIn(p.readyAt), 'err'); return; }
    const phrases = HLavka.phrases();
    const people = ((state && state.online) || []).filter((n) => !sameNick(n, me.nick));
    let to = '*';
    let phrase = phrases[0] ? phrases[0].key : '';
    const wrap = document.createElement('div');
    wrap.className = 'modal dedmodal';
    wrap.innerHTML = `<div class="card">
        <h3>💌 Присвятити пісню</h3>
        <div class="muted small">Перед «${esc(it.track.title)}» ${esc(dj())} скаже в ефір, кому ти її присвячуєш. Присвята — раз на 3 години.</div>
        <div class="ded-h">Кому</div>
        <div class="ded-chips" data-k="to">${['*'].concat(people).map((n) => `<button type="button" class="chip${n === '*' ? ' on' : ''}" data-v="${esc(n)}">${n === '*' ? '🌍 усім, хто слухає' : esc(n)}</button>`).join('')}</div>
        ${people.length ? '' : '<div class="muted small">Зараз на сайті більше ні душі — присвяти всім, хто слухає.</div>'}
        <div class="ded-h">Як</div>
        <div class="ded-chips" data-k="phrase">${phrases.map((x, i) => `<button type="button" class="chip${i === 0 ? ' on' : ''}" data-v="${esc(x.key)}">${esc(x.text)}</button>`).join('')}</div>
        <div class="ded-say"></div>
        <div class="row"><button type="button" class="primary" data-yes>Присвятити</button><button type="button" class="ghost" data-no>Не треба</button></div>
      </div>`;
    const say = () => {
      const ph = phrases.find((x) => x.key === phrase);
      wrap.querySelector('.ded-say').textContent = `🎙 «Цю пісню ${me.nick} присвячує ${to === '*' ? 'всім, хто слухає' : HLavka.dative(to)}${ph ? ' — ' + ph.text : ''}»`;
    };
    const close = () => { wrap.remove(); document.removeEventListener('keydown', onKey, true); };
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
    wrap.querySelectorAll('.ded-chips').forEach((box) => box.querySelectorAll('button').forEach((b) => b.onclick = () => {
      box.querySelectorAll('button').forEach((x) => x.classList.toggle('on', x === b));
      if (box.dataset.k === 'to') to = b.dataset.v; else phrase = b.dataset.v;
      say();
    }));
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    wrap.querySelector('[data-no]').onclick = close;
    wrap.querySelector('[data-yes]').onclick = (e) => busy(e.currentTarget, 'присвячую…', async () => {
      try {
        const r = await api('POST', '/api/lavka/dedicate', { to, phrase });
        toast(r.message || 'Присвята полетіла', 'ok');
        HLavka.usedPerk('dedication', 180);
        close();
      } catch (err) { fail(err); }
    });
    say();
    document.body.appendChild(wrap);
    document.addEventListener('keydown', onKey, true);
    wrap.querySelector('[data-yes]').focus();
  }

  // ---------- queue ----------
  /// Чип стану — лише коли щось не так чи ще не готово: «готово» й «чекає» людям нічого не кажуть,
  /// їм важливо, коли заграє, а це й так видно (ETA в рядку).
  function statusChip(it) {
    switch (it.status) {
      case 'downloading': return '<span class="chip warn"><span class="spin"></span> качається</span>';
      case 'failed': return `<span class="chip err">${esc(it.error || 'халепа')}</span>`;
    }
    return '';
  }

  function renderQueue() {
    if (dragging?.active) { pendingQueueRender = true; return; } // finish the drag first, then redraw from the newest state
    const ul = $('queue');
    const q = state.queue;
    queueDur = q.map((it) => it.track.durationSec || 0);
    const sug0 = (state.suggestions || [])[0];
    // Присвята лягає перед МОЄЮ найближчою піснею, що ще не пішла в ефір, — тож і 💌 лише на ній.
    const dedId = HLavka.perk('dedication')?.owned
      ? (q.find((it) => sameNick(it.requestedBy, me.nick) && it.status !== 'dispatched' && it.status !== 'failed' && !isVoice(it.track)) || {}).itemId : null;
    const sig = JSON.stringify([q.map((it) => [it.itemId, it.status, it.error, it.requestedBy, it.via]), me.role, me.nick, state.djName, !q.length && sug0 && sug0.itemId, dedId]);
    if (sig === queueSig) { tick(); return; }
    queueSig = sig;
    $('queueCount').textContent = q.length ? `· ${q.length} · ${fmt(queueDur.reduce((a, b) => a + b, 0))}` : '';
    if (!q.length) {
      // Порожня черга — привід не виправдовуватись, а запропонувати перше, що Глек уже підібрав.
      ul.innerHTML = `<li class="empty queue-empty">
        <img src="/static/glek.svg" alt="">
        <div>Порожньо. Закинь щось, або хай ${esc(dj())} крутить своє.
        ${sug0 ? `<div class="qe-btn"><button id="qTakeSug" class="primary" title="${esc(`${sug0.track.artist} — ${sug0.track.title}`)}">👍 Закинути перше з порад ${esc(djGen())}</button></div>` : ''}</div>
      </li>`;
      if (sug0) $('qTakeSug').onclick = (e) => busy(e.currentTarget, 'закидаю…', () => api('POST', `/api/suggest/${sug0.itemId}/add`).then(ok).catch(fail));
    } else {
      const now = Date.now();
      ul.innerHTML = q.map((it, i) => {
        const mine = sameNick(it.requestedBy, me.nick) || me.role === 'admin';
        const canMove = mine && it.status !== 'dispatched' && q.length > 1;
        const fresh = now - new Date(it.addedAt).getTime() < 4000;
        return `<li class="qitem ${it.status} ${fresh ? 'fresh' : ''} ${canMove ? 'movable' : ''}" data-id="${it.itemId}">
          <div class="n">${i + 1}</div>
          ${cover(it.track, 'draggable="false"')}
          <div style="min-width:0">
            <div class="t">${esc(it.track.title)}</div>
            <div class="a">${esc(it.track.artist)} · ${fmt(it.track.durationSec)}</div>
            <div class="meta">${nickHtml(it.requestedBy, 'qnick')}${it.via === 'suggestion' ? `<span class="chip dj">порада ${esc(djGen())}</span>` : ''}${statusChip(it)}<span class="eta" data-eta="${i}"></span></div>
          </div>
          <div class="btns">
            ${voiceBtn(it.track)}
            ${it.itemId === dedId ? `<button class="icon ded" title="Присвятити цю пісню — ${esc(dj())} скаже в ефір, кому, перед тим як вона заграє">💌</button>` : ''}
            ${canMove ? '<span class="grip" title="Тягни, щоб пересунути">⠿</span>' : ''}
            ${mine ? `<button class="icon danger rm" title="Прибрати">✕</button>` : ''}
          </div>
        </li>`;
      }).join('');
      ul.querySelectorAll('li').forEach((li) => {
        const id = li.dataset.id;
        li.querySelector('.rm')?.addEventListener('click', (e) => busy(e.currentTarget, '', () => api('DELETE', `/api/queue/${id}`).catch(fail)));
        li.querySelector('.ded')?.addEventListener('click', () => openDedication(q.find((x) => String(x.itemId) === id)));
        wireVoiceButtons(li);
        if (li.classList.contains('movable')) li.addEventListener('pointerdown', (e) => startDrag(e, li));
      });
    }
    tick();
  }

  // ---------- drag to reorder: grab a row (mouse) or its ⠿ grip (touch) and pull it where it should play ----------
  function startDrag(e, li) {
    if (e.button !== 0 || dragging) return;
    if (e.target.closest('button, a, input')) return;
    if (e.pointerType === 'touch' && !e.target.closest('.grip')) return; // a finger on the row scrolls the page; only the grip drags
    const lis = [...$('queue').querySelectorAll('li.qitem')];
    const from = lis.indexOf(li);
    const min = lis[0]?.classList.contains('dispatched') ? 1 : 0; // the next track already sits in liquidsoap, nothing goes before it
    if (from < min) return;
    const gs = getComputedStyle($('queue'));
    dragging = {
      li, id: li.dataset.id, lis, from, to: from, min, max: lis.length - 1, active: false,
      rects: lis.map((el) => { const r = el.getBoundingClientRect(); return { top: r.top + window.scrollY, h: r.height }; }),
      gap: parseFloat(gs.rowGap || gs.gap) || 0,
      startY: e.clientY + window.scrollY, lastY: e.clientY, scrollV: 0, raf: 0,
    };
    try { li.setPointerCapture(e.pointerId); } catch { /* synthetic or already released pointer */ }
    window.addEventListener('pointermove', onDragMove);
    window.addEventListener('pointerup', endDrag);
    window.addEventListener('pointercancel', endDrag);
  }
  function onDragMove(e) {
    const d = dragging;
    if (!d) return;
    d.lastY = e.clientY;
    const dy = e.clientY + window.scrollY - d.startY;
    if (!d.active) {
      if (Math.abs(dy) < 6) return; // a click is not a drag
      d.active = true;
      d.li.classList.add('dragging');
      document.body.classList.add('is-dragging');
      d.lis.forEach((el) => { if (el !== d.li) el.classList.add('shift'); });
    }
    e.preventDefault();
    updateDrag(dy);
    // near the top or bottom of the window the page scrolls by itself
    const edge = 70, vh = window.innerHeight;
    d.scrollV = e.clientY < edge ? -(edge - e.clientY) / 5 : e.clientY > vh - edge ? (e.clientY - (vh - edge)) / 5 : 0;
    if (d.scrollV && !d.raf) d.raf = requestAnimationFrame(scrollStep);
  }
  function scrollStep() {
    const d = dragging;
    if (!d) return;
    d.raf = 0;
    if (!d.scrollV) return;
    window.scrollBy(0, d.scrollV);
    updateDrag(d.lastY + window.scrollY - d.startY);
    d.raf = requestAnimationFrame(scrollStep);
  }
  function updateDrag(dy) {
    const d = dragging;
    d.li.style.transform = `translateY(${dy}px) scale(1.02)`;
    // the row lands where its centre is: count the others whose centre is above it
    const centre = d.rects[d.from].top + d.rects[d.from].h / 2 + dy;
    let to = d.min;
    for (let i = d.min; i <= d.max; i++) if (i !== d.from && d.rects[i].top + d.rects[i].h / 2 < centre) to++;
    d.to = to;
    // the others slide out of the way by exactly one row
    const shift = d.rects[d.from].h + d.gap;
    d.lis.forEach((el, i) => {
      if (i === d.from) return;
      const t = i < d.from && i >= to ? shift : i > d.from && i <= to ? -shift : 0;
      el.style.transform = t ? `translateY(${t}px)` : '';
    });
  }
  async function endDrag() {
    const d = dragging;
    if (!d) return;
    dragging = null;
    window.removeEventListener('pointermove', onDragMove);
    window.removeEventListener('pointerup', endDrag);
    window.removeEventListener('pointercancel', endDrag);
    if (d.raf) cancelAnimationFrame(d.raf);
    document.body.classList.remove('is-dragging');
    d.lis.forEach((el) => { el.style.transform = ''; el.classList.remove('shift'); });
    d.li.classList.remove('dragging');
    if (!d.active) return;
    const from = state.queue.findIndex((x) => x.itemId === d.id);
    if (from >= 0 && d.to !== from) {
      // show it in place at once; the server's next broadcast confirms (or corrects) it
      const [item] = state.queue.splice(from, 1);
      state.queue.splice(d.to, 0, item);
      queueSig = '';
      renderQueue();
      $('queue').querySelector(`li[data-id="${d.id}"]`)?.classList.add('moved');
      try { await api('POST', `/api/queue/${d.id}/move`, { toIndex: d.to }); }
      catch (err) { fail(err); }
    } else if (pendingQueueRender) {
      queueSig = '';
      renderQueue();
    }
    pendingQueueRender = false;
  }

  // ---------- DJ suggestions: four fixed slots built from the track on air; the first card is what really plays next ----------
  const SLOTS = 4;
  let sugSeen = new Set(); // cards already on screen: only newcomers get the fade-in, so the block does not blink
  function renderSuggestions() {
    const box = $('sugCards');
    $('djName').textContent = dj();
    const list = state.suggestions || [];
    const a = state.autoNext;
    const seed = state.suggestSeed;
    const sig = JSON.stringify([list.map((s) => s.itemId), a && [a.itemId, a.status, a.error], seed && seed.id, state.suggestSeedNote, state.now.track && state.now.track.id, state.queue.length, state.djName]);
    if (sig === sugSig) { tick(); return; }
    sugSig = sig;
    const label = (t) => (t.artist ? `${t.artist} — ${t.title}` : t.title);
    const onAir = seed && state.now.track && state.now.track.id === seed.id;
    $('djSub').textContent = seed
      ? `Підбирає під ${state.suggestSeedNote || (onAir ? 'те, що зараз шкварить' : 'останнє, що грало')}: ${label(seed)}`
      : 'Підбирає під те, що зараз шкварить. Зміниться трек — зміняться й поради';
    // Картка — один рядок: обкладинка, назва, дві кнопки праворуч. Раніше це був блок на 118 px із кнопками
    // під назвою, і на ноутбуці поради опинялись аж на другому екрані.
    const card = (s, next) => `<div class="sug ${next ? 'next' : ''} ${sugSeen.has(s.itemId) ? '' : 'fade'}" data-id="${s.itemId}">
        ${s.track.thumbUrl ? `<img src="${esc(s.track.thumbUrl)}" alt="">` : '<div class="noimg"></div>'}
        <div class="st">
          <div class="t" title="${esc(label(s.track))}">${esc(s.track.title)}</div>
          <div class="a">${esc(s.track.artist)} · ${fmt(s.track.durationSec)}</div>
          ${next
            ? `<div class="r">${state.queue.length ? 'Після черги' : 'Далі'} <span data-eta-after></span>, якщо ніхто нічого не закине${s.reason && !onAir ? ' · ' + esc(s.reason) : ''}</div>`
            : (s.reason && !onAir ? `<div class="r">${esc(s.reason)}</div>` : '')}
        </div>
        <div class="btns">
          ${next ? statusChip(s) : '<button class="primary add" title="Закинути в чергу">👍 Беру</button>'}
          <button class="skip" title="${next ? 'Хай поставить щось інше' : 'Прибрати, хай запропонує інше'}">👎${next ? ' Не те' : ''}</button>
        </div>
      </div>`;
    const cards = (a ? [card(a, true)] : []).concat(list.map((s) => card(s, false)));
    // Порожнє місце крутить спінер лише тоді, коли порад нема зовсім: вічне «шукає ще…» поряд із готовими — шум.
    if (!cards.length) cards.push(`<div class="sug empty"><span class="spin"></span> ${esc(dj())} порпається на полицях…</div>`);
    box.innerHTML = cards.slice(0, SLOTS).join('');
    sugSeen = new Set((a ? [a.itemId] : []).concat(list.map((s) => s.itemId)));
    box.querySelectorAll('.sug[data-id]').forEach((el) => {
      const id = el.dataset.id;
      el.querySelector('.add')?.addEventListener('click', (e) => busy(e.currentTarget, 'закидаю…', () => api('POST', `/api/suggest/${id}/add`).then(ok).catch(fail)));
      el.querySelector('.skip')?.addEventListener('click', (e) => busy(e.currentTarget, 'шукаю…', () => api('POST', `/api/suggest/${id}/skip`).catch(fail)));
    });
    tick();
  }

  // Хто в навушниках: ніки з плеєром на сайті плюс решта підключень до потоку (ETS2, VLC), яких по імені не видно.
  function listenersText() {
    const nicks = state.listeningNicks || [];
    const others = Math.max(0, (state.listeners || 0) - (state.listeningTabs || 0));
    const parts = [];
    if (nicks.length) parts.push('Слухають: ' + nicks.join(', '));
    if (others && state.listeningTabs !== undefined) parts.push(`${nicks.length ? 'ще ' : 'Слухають '}${others} через ETS2/VLC/інший плеєр`);
    else if (others) parts.push(`Слухають ${others}`);
    return parts.join('; ') || 'У навушниках — ні душі';
  }

  // 🎧 у шапці: число і стос кружечків замість імен (записка #21) — фото чи значок із Лавки, а без вигляду порожній
  // кружечок у кольорі ніка. Імена — у підказці й тості, клік по кружечку — картка людини (data-who, web/people.js).
  // Понад шість — «+N»; на вужчій шапці кружечків менше (там і гучність ховається за 🔊), щоб не тіснити назву треку.
  const lsMid = window.matchMedia('(max-width: 1440px)'), lsNarrow = window.matchMedia('(max-width: 1000px)');
  let listenersSig = '';
  function listenersHtml(shown, nicks) {
    const max = lsNarrow.matches ? 3 : lsMid.matches ? 4 : 6;
    const vis = nicks.length > max ? nicks.slice(0, max - 1) : nicks;
    const more = nicks.length - vis.length;
    const faces = vis.map((n) => `<button type="button" class="ls-who" data-who="${esc(n)}" title="${esc(n)} слухає ефір" aria-label="${esc(n)}">${HPeople.ava(n, 'ava ls blank')}</button>`).join('');
    return `<span class="ls-n">🎧 ${shown}</span>`
      + (nicks.length ? `<span class="ls-stack">${faces}${more ? `<span class="ls-more">+${more}</span>` : ''}</span>` : '');
  }
  for (const m of [lsMid, lsNarrow]) m.addEventListener?.('change', () => { if (state) renderOnline(); });

  function renderOnline() {
    const nicks = state.listeningNicks || [];
    const listens = (n) => nicks.some((x) => sameNick(x, n));
    const shown = Math.max(state.listeners || 0, nicks.length);
    const chip = $('listeners');
    // Стан приходить часто, а кружечки з фото — та сама розмітка: не перебудовуємо, коли нічого не змінилось.
    const html = listenersHtml(shown, nicks);
    if (html !== listenersSig) { listenersSig = html; chip.innerHTML = html; }
    chip.title = listenersText();
    chip.classList.toggle('on', shown > 0);
    state.online.forEach(learnNick);
    const people = state.online.slice().sort((a, b) => listens(b) - listens(a));
    // Клік по людині — її картка (web/people.js ловить data-who на всій сторінці).
    // 🎙 — людина в Посиденьках чи в голосі столу (web/voice.js).
    const talks = (n) => (window.HVoice && HVoice.inVoice(n) ? '<span class="vc-mark" title="у голосі">🎙</span>' : '');
    $('online').innerHTML = people.map((n) => listens(n)
      ? `<button type="button" class="chip listening who-n${HPeople.nickCls(n)}" data-who="${esc(n)}" style="--h:${HPeople.hue(n)}" title="${esc(n)} зараз слухає ефір">🎧 ${talks(n)}${HPeople.badge(n)}${esc(n)}${crownOf(n)}</button>`
      : `<button type="button" class="chip who-n${HPeople.nickCls(n)}" data-who="${esc(n)}" style="--h:${HPeople.hue(n)}" title="тусить на сайті, але плеєр вирублений">${talks(n)}${HPeople.badge(n)}${esc(n)}${crownOf(n)}</button>`).join('') || '<span class="muted small">ні душі</span>';
    HPeople.refreshWhere();          // на відкритому профілі «на сайті / слухає» — живе
  }
  // Клік по кружечку — картка людини (її відкриває people.js), по решті чипа — хто саме слухає.
  $('listeners').onclick = (e) => { if (state && !e.target.closest('[data-who]')) toast(listenersText()); };

  function render() {
    if (!state) return;
    $('siteName').textContent = state.siteName;
    $('micBtn').hidden = !state.voiceMaxSeconds || !canRecord();   // без https мікрофона браузер не дасть, нема чого й дражнити
    renderNow();
    renderQueue();
    renderOnline();
    renderSuggestions();
    if (album && albumSig() !== album.sig) drawAlbum();   // «у черзі» біля треків альбому
    if (state.now.playId !== lastPlayId) {
      lastPlayId = state.now.playId;
      if (route === 'lib' && (libTab === 'history' || libTab === 'bans' || libTab === 'ads')) loadLib();
    }
  }

  // ---------- кубик і команди чату ----------
  // Нова команда: рядок сюди і гілка в ChatCommands.Run на сервері. Головне ім'я — українське: з української
  // розкладки латинські /roll і /coin набирати незручно. quick — кнопки палітри, що кидають одним натиском;
  // template — що підставити в поле для команд з аргументами.
  const COMMANDS = [
    { cmd: '/кубик', alias: ['/roll'], args: '[N | A-B]', help: 'жбурнути кубик 1–6; «/кубик 100» — до ста, «/кубик 2-12» — свої межі',
      quick: [['🎲 1–6', '/кубик'], ['🎲 до 100', '/кубик 100']] },
    { cmd: '/монетка', alias: ['/coin'], args: '', help: 'жбурнути монетку: орел чи решка', quick: [['🪙 Монетка', '/монетка']] },
    { cmd: '/обери', alias: ['/вибери', '/choose'], args: 'а, б або в', help: 'обрати за тебе: «/обери чай, кава або компот»', template: '/обери ' },
    { cmd: '/куля', alias: ['/глек', '/8ball'], args: 'питання', help: 'спитати Дядька Глека: «/куля чи буде дощ?»', template: '/куля ' },
    { cmd: '/клич', alias: ['/поклич', '/invite'], args: '@нік', help: 'гукнути людину за твій стіл, що чекає гравців', template: '/клич @' },
    { cmd: '/столи', alias: ['/стіл', '/tables'], args: '', help: 'живі столи з кнопками — бачиш лише ти', quick: [['🎲 Столи', '/столи']] },
    { cmd: '/пароль', args: 'нік новий_пароль', help: 'поставити людині новий пароль, коли вона свій забула. Лише адмін', admin: true, template: '/пароль ' },
  ];
  const cmdNames = (c) => [c.cmd, ...(c.alias || [])];
  /// «.кубик 20» з української розкладки — це «/кубик 20»: крапка там, де в англійській «/». Лише для назв команд.
  function dotToSlash(text) {
    const m = /^\.(\p{L}+)(?=\s|$)/u.exec(text || '');
    if (!m) return text;
    const name = '/' + m[1].toLowerCase();
    return COMMANDS.some((c) => cmdNames(c).includes(name)) ? '/' + text.slice(1) : text;
  }
  // Грані малюємо крапками самі: юнікодні ⚀⚁⚂ у кожному шрифті сидять у своєму квадраті по-своєму
  // і в плитці стоять криво. Індекси — клітинки сітки 3×3 зліва направо.
  const PIPS = { 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
  const isFace = (min, max) => min === 1 && max === 6;
  const pipsHtml = (v) => Array.from({ length: 9 }, (_, i) => `<i${PIPS[v].includes(i) ? ' class="on"' : ''}></i>`).join('');
  function paintDie(el, v, min, max) {
    if (isFace(min, max)) el.innerHTML = pipsHtml(v); else el.textContent = String(v);
  }

  /// Кубик падає згори і крутиться, поки не вляжеться на своє число.
  function rollDie(el, min, max, value) {
    el.classList.add('rolling');
    const until = performance.now() + 850;
    const tick = () => {
      if (performance.now() >= until) {
        paintDie(el, value, min, max);
        el.classList.remove('rolling');
        el.classList.add('landed');
        return;
      }
      paintDie(el, min + Math.floor(Math.random() * (max - min + 1)), min, max);
      setTimeout(tick, 70);
    };
    tick();
  }

  /// Монетка крутиться 0.8 с, і лише тоді видно, чим вона впала: результат приходить із сервера
  /// одразу, тож інтрига — це єдине, що фронт тут може додати. Анімація — Web Animations API,
  /// щоб не чіпати спільний style.css заради однієї команди.
  const COIN_MS = 800;
  function flipCoin(el, label) {
    label.style.visibility = 'hidden';
    if (el.animate) {
      el.animate([
        { transform: 'rotateX(0) scale(.8)' },
        { transform: 'rotateX(900deg) scale(1.2)', offset: .55 },
        { transform: 'rotateX(1800deg) scale(1)' },
      ], { duration: COIN_MS, easing: 'cubic-bezier(.3, 1.1, .5, 1)' });
    }
    setTimeout(() => { label.style.visibility = ''; }, COIN_MS);
  }

  // ---------- палітра й підказка команд ----------
  // Кнопка «/» — палітра: кубик, монетка й столи кидаються одним натиском (з телефона набирати їх незручно),
  // решта ставить у поле заготовку. Набираєш «/ку» чи «.ку» — список звужується, ↑↓ вибирають, Tab чи Enter
  // дописують назву; коли назва вже повна, Enter просто шле.
  let cmdList = [];          // що зараз у списку (для ↑↓/Tab)
  let cmdSel = 0;
  const cmdAllowed = (c) => !c.admin || me.role === 'admin';
  /// Команди, що підходять під перше слово поля: «/» — усі, «/ку» — кубик і куля, «.ку» — те саме з крапкою.
  function cmdMatches(word) {
    const w = word.toLowerCase();
    const dot = w.startsWith('.');
    const q = w.slice(1);
    if (dot && !/^\p{L}+$/u.test(q)) return [];          // «...» чи «.5» — це не команда
    return COMMANDS.filter((c) => cmdAllowed(c) && cmdNames(c).some((n) => n.slice(1).startsWith(q)));
  }
  function sendCommand(text) {
    if (!conn) { askNick(true); return; }
    conn.invoke('SendChat', text).then((err) => { if (err) toast(err, 'err'); }).catch((e) => toast('Халепа: не відправилось — ' + e.message, 'err'));
  }
  function cmdRow(c, i) {
    return `<div class="cmd${i === cmdSel ? ' on' : ''}" data-i="${i}">
        <b>${esc(c.cmd)}</b> <span class="muted small">${esc(c.args)}</span>
        <div class="muted small">${esc(c.help)}${c.alias ? ` · ще ${esc(c.alias.join(', '))}` : ''}</div>
      </div>`;
  }
  /// Палітра по кнопці: швидкі кидки згори, під ними всі команди.
  function showPalette() {
    const box = $('cmdHint');
    cmdList = COMMANDS.filter(cmdAllowed);
    cmdSel = -1;
    const quick = COMMANDS.filter(cmdAllowed).flatMap((c) => c.quick || []);
    box.innerHTML = `<div class="cmdquick">${quick.map(([label, text]) => `<button type="button" data-send="${esc(text)}">${esc(label)}</button>`).join('')}</div>`
      + cmdList.map(cmdRow).join('');
    wireCmdBox(box);
    box.hidden = false;
  }
  /// Підказка під час набору: лише ті, що підходять, або — коли вже пишуться аргументи — як ними користуватись.
  function showCmdHint(typed) {
    const box = $('cmdHint');
    const word = typed.split(' ')[0];
    const list = cmdMatches(word);
    if (!list.length) { hideCmdHint(); return; }
    const args = typed.includes(' ');
    cmdList = args ? [] : list;
    if (!args) cmdSel = Math.min(Math.max(cmdSel, 0), list.length - 1);
    box.innerHTML = (args ? list.slice(0, 1).map((c) => cmdRow(c, -1)) : list.map(cmdRow)).join('');
    wireCmdBox(box);
    box.hidden = false;
  }
  function wireCmdBox(box) {
    box.querySelectorAll('[data-send]').forEach((b) => b.onclick = () => { sendCommand(b.dataset.send); hideCmdHint(); });
    box.querySelectorAll('.cmd[data-i]').forEach((el) => el.onmousedown = (e) => { e.preventDefault(); pickCmd(cmdList[+el.dataset.i] || COMMANDS.find(cmdAllowed)); });
  }
  /// Вибрали команду: без аргументів (монетка, столи) — одразу кидаємо; з ними — заготовка в поле.
  function pickCmd(c) {
    if (!c) return;
    const inp = $('chatInput');
    if (!c.args) { inp.value = ''; hideCmdHint(); sendCommand(c.cmd); return; }
    inp.value = c.template || c.cmd + ' ';
    inp.focus();
    inp.setSelectionRange(inp.value.length, inp.value.length);
    showCmdHint(inp.value);
  }
  const hideCmdHint = () => { $('cmdHint').hidden = true; cmdList = []; cmdSel = 0; };
  $('cmdBtn').onclick = () => { hideEmoji(); $('cmdHint').hidden ? showPalette() : hideCmdHint(); };
  $('chatInput').addEventListener('input', () => {
    const v = $('chatInput').value;
    if (/^[/.]/.test(v)) showCmdHint(v); else hideCmdHint();
  });
  $('chatInput').addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { hideCmdHint(); hideEmoji(); return; }
    if ($('cmdHint').hidden || !cmdList.length) return;
    const inp = $('chatInput');
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      cmdSel = (Math.max(cmdSel, 0) + (e.key === 'ArrowDown' ? 1 : -1) + cmdList.length) % cmdList.length;
      $('cmdHint').querySelectorAll('.cmd[data-i]').forEach((el) => el.classList.toggle('on', +el.dataset.i === cmdSel));
      return;
    }
    const word = inp.value.split(' ')[0].toLowerCase().replace(/^\./, '/');
    const exact = COMMANDS.some((c) => cmdAllowed(c) && cmdNames(c).includes(word));
    // Tab дописує завжди; Enter — лише коли назва ще неповна, інакше це «відправити» (обробляє форма).
    // У палітрі з порожнім полем обидва чекають, поки людина стрілками щось вибере.
    if ((e.key === 'Tab' || (e.key === 'Enter' && !exact)) && (cmdSel >= 0 || inp.value)) {
      e.preventDefault();
      e.stopImmediatePropagation();
      pickCmd(cmdList[Math.max(cmdSel, 0)]);
    }
  });

  // ---------- смайли ----------
  // Панель відкривається над рядком вводу; клік ставить смайл туди, де стоїть курсор,
  // і панель лишається відкритою — щоб можна було накидати кілька підряд.
  $('emojiPick').innerHTML = EMOJI_GROUPS.map((g) => `<div class="egroup">
      <div class="muted small">${esc(g.name)}</div>
      <div class="erow">${g.list.map((e) => `<button type="button" data-e="${esc(e)}">${e}</button>`).join('')}</div>
    </div>`).join('');
  $('emojiPick').querySelectorAll('button').forEach((b) => b.onclick = () => putEmoji(b.dataset.e));
  const hideEmoji = () => { $('emojiPick').hidden = true; $('emojiBtn').classList.remove('on'); };
  function showEmoji() {
    hideCmdHint();
    $('emojiPick').hidden = false;
    $('emojiBtn').classList.add('on');
    $('chatInput').focus();
  }
  function putEmoji(e) {
    const inp = $('chatInput');
    const [from, to] = [inp.selectionStart ?? inp.value.length, inp.selectionEnd ?? inp.value.length];
    const text = inp.value.slice(0, from) + e + inp.value.slice(to);
    if (text.length > inp.maxLength) { toast('Халепа: задовге повідомлення', 'err'); return; }
    inp.value = text;
    inp.focus();
    inp.setSelectionRange(from + e.length, from + e.length);
  }
  $('emojiBtn').onclick = () => ($('emojiPick').hidden ? showEmoji() : hideEmoji());
  document.addEventListener('click', (e) => {
    // Поки клацаєш у самому чаті (вводиш, шлеш) — панель не зачиняється; клік по решті сторінки її прибирає.
    if (!$('emojiPick').hidden && !e.target.closest('#emojiPick, #chatForm')) hideEmoji();
  });

  // ---------- chat + log ----------
  const linkify = (s) => esc(s).replace(/(https?:\/\/[^\s<]+)/g, (m) => `<a href="${m}" target="_blank" rel="noopener">${m}</a>`);
  // Повідомлення справді видно, коли відкрита вкладка «Балачки», сама панель не згорнута
  // (на телефоні — коли стоїмо на вкладці балачок) і вкладка браузера на передньому плані.
  function chatVisible() {
    if (chatTab !== 'chat' || document.hidden) return false;
    return isMobile() ? route === 'chat' : chatOpen;
  }
  function setUnread(n) {
    unread = n;
    for (const id of ['chatBadge', 'mChatBadge', 'hdrChatBadge']) { const b = $(id); b.hidden = !n; b.textContent = n; }
    paintTitle();
  }
  /// Заголовок вкладки: трек плюс «(3)», поки непрочитане нікуди не поділось, і «🎲 Твій хід», коли за
  /// якимось столом чекають на тебе, а ти деінде.
  let turnRooms = [];
  function paintTitle() { document.title = (turnRooms.length ? '🎲 Твій хід · ' : '') + (unread ? `(${unread}) ` : '') + baseTitle; }
  /// Каркас ігор каже, за якими столами зараз мій хід, а я не дивлюсь на них. Новий такий стіл — дзінь.
  function onTurn(list) {
    const was = new Set(turnRooms.map((x) => x.id));
    turnRooms = list || [];
    paintTitle();
    const nav = document.querySelector('#mainNav button[data-route="games"]');
    if (nav) { nav.classList.toggle('turn', turnRooms.length > 0); nav.title = turnRooms.length ? 'Твій хід: ' + turnRooms.map((x) => x.title).join(', ') : 'Столи й ігри — клавіша 3'; }
    $('mNavGames').hidden = !turnRooms.length;
    if (turnRooms.some((x) => !was.has(x.id))) ping();
  }
  /// Кличуть на ім'я — навіть коли балачки згорнуті, це має долетіти.
  const mentionsMe = (text) => !!me.nick && me.nick.length > 1 && String(text || '').toLowerCase().includes(me.nick.toLowerCase());
  /// Тегнули саме через @ — це вже не просто згадка в розмові, а поклик: на нього й звук.
  const taggedMe = (text) => !!me.nick && String(text || '').toLowerCase().includes('@' + me.nick.toLowerCase());
  /// 👑 біля ніка чинного чемпіона турніру.
  /// 🏆 — «Голова вечірки» (Глечикова вечірка, specs/vechirka.md К5): ніки з /api/games/vechirka/crown; модуль вечірки кличе refresh після фіналу.
  let partyCrown = new Set();
  const loadPartyCrown = () => fetch('/api/games/vechirka/crown').then((r) => (r.ok ? r.json() : null)).then((j) => { if (j && j.nicks) partyCrown = new Set(j.nicks.map((n) => String(n).toLowerCase())); }).catch(() => {});
  window.HPartyCrown = { refresh: loadPartyCrown };
  loadPartyCrown();
  const crownOf = (nick) => (window.HTournament && HTournament.crowned(nick) ? '<span class="crown" title="Чемпіон турніру">👑</span>' : '')
    + (partyCrown.has(String(nick).toLowerCase()) ? '<span class="crown" title="Голова вечірки">🏆</span>' : '');

  /// Ніки, які сайт знає: хто зараз онлайн і хто писав у балачках. З них — підказка після @ і підсвітка.
  const knownNicks = new Map();          // нижній регістр → як пишеться
  function learnNick(n) { if (n && n.length > 1) knownNicks.set(n.toLowerCase(), n); }

  /// @Нік у тексті — підсвітити. Працює по вже екранованому HTML: ніки теж екрануємо, довші спершу («Оля Петрівна» раніше «Оля»).
  function highlightMentions(html) {
    const nicks = [...knownNicks.values()].sort((a, b) => b.length - a.length);
    if (!nicks.length || html.indexOf('@') < 0) return html;
    const escRe = (x) => x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const re = new RegExp('@(' + nicks.map((n) => escRe(esc(n))).join('|') + ')(?![\\p{L}\\p{N}_])', 'giu');
    return html.replace(re, (m, n) => `<span class="mention${sameNick(n, esc(me.nick)) ? ' me' : ''}">${m}</span>`);
  }

  // ---------- звук на поклик ----------
  // Коли тебе тегнули через @ або відповіли — коротке «дзінь» (два тони через WebAudio, без файлів). Вимикається 🔔.
  let pingOn = localStorage.getItem('pingSound') !== '0';
  let audioCtx = null;
  function ping() {
    if (!pingOn) return;
    try {
      audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
      if (audioCtx.state === 'suspended') audioCtx.resume();
      const t0 = audioCtx.currentTime;
      [[880, 0], [1320, 0.12]].forEach(([freq, at]) => {
        const o = audioCtx.createOscillator();
        const g = audioCtx.createGain();
        o.type = 'sine';
        o.frequency.value = freq;
        g.gain.setValueAtTime(0.0001, t0 + at);
        g.gain.exponentialRampToValueAtTime(0.18, t0 + at + 0.015);
        g.gain.exponentialRampToValueAtTime(0.0001, t0 + at + 0.35);
        o.connect(g).connect(audioCtx.destination);
        o.start(t0 + at);
        o.stop(t0 + at + 0.4);
      });
    } catch { /* браузер без WebAudio — мовчки */ }
  }
  // Браузер дає звук лише після першого жесту на сторінці — будимо контекст на першому ж кліку/клавіші.
  const wakeAudio = () => {
    try { audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)(); if (audioCtx.state === 'suspended') audioCtx.resume(); } catch { /* нема — то й нема */ }
  };
  document.addEventListener('pointerdown', wakeAudio, { once: true });
  document.addEventListener('keydown', wakeAudio, { once: true });
  // Той самий перший жест відмикає й плеєр гімнів (нижче, unlockAnthem): iOS/Safari дає звук без жесту лише елементу,
  // який уже раз грав із жесту, — а гімн за столом приходить подією з сервера, коли пальця на екрані нема.
  document.addEventListener('pointerdown', () => unlockAnthem(), { once: true, capture: true });
  document.addEventListener('keydown', () => unlockAnthem(), { once: true, capture: true });
  function paintPing() {
    const b = $('pingBtn');
    b.textContent = pingOn ? '🔔' : '🔕';
    b.title = pingOn ? 'Дзінь, коли тебе гукнули чи відповіли, — увімкнено' : 'Дзінь на @ і відповіді вирублено';
  }
  $('pingBtn').onclick = () => {
    pingOn = !pingOn;
    try { localStorage.setItem('pingSound', pingOn ? '1' : '0'); } catch { /* приватне вікно */ }
    paintPing();
    if (pingOn) ping();
  };
  paintPing();

  // ---------- 🎺 гімн переможця (docs/games/specs/anthem.md §4) ----------
  // Одне місце на весь сайт: стіл (games/core.js), Лавка й профіль лише кличуть playAnthem. HTMLAudio, а не WebAudio:
  // свій трек людини — mp3 з сервера, його треба стрімити, а не декодувати. Гучність — як у радіо; радіо на час гімну
  // притишується до 20 %. Другий гімн за столом, поки грає перший, не перебиває його; прослуховування в Лавці — перебиває
  // (це людина тисне сама), і друге натискання на той самий ▶ зупиняє.
  const ANTHEM_DUCK = 0.2;
  const ANTHEM_MAX_MS = 16000;    // запобіжник: навіть якщо ended не прийде (обірваний потік), радіо повернеться
  const anthemSound = () => { try { return localStorage.getItem('anthemSound') !== '0'; } catch { return true; } };
  // Елемент — одразу, а не на першому гімні: відмикати жестом треба саме той, що гратиме.
  try { anthemEl = new Audio(); } catch { /* браузер без HTMLAudio — гімнів не буде */ }
  /// Тиша для відмикання: 0,05 с WAV 8 кГц 8 біт моно (400 семплів 0x80), зібрана тут же — без файла й без мережі.
  function silentWav() {
    const n = 400;
    const b = new Uint8Array(44 + n);
    const v = new DataView(b.buffer);
    const tag = (at, t) => { for (let i = 0; i < 4; i++) b[at + i] = t.charCodeAt(i); };
    tag(0, 'RIFF'); v.setUint32(4, 36 + n, true); tag(8, 'WAVE');
    tag(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
    v.setUint32(24, 8000, true); v.setUint32(28, 8000, true); v.setUint16(32, 1, true); v.setUint16(34, 8, true);
    tag(36, 'data'); v.setUint32(40, n, true);
    b.fill(0x80, 44);
    let bin = '';
    for (let i = 0; i < b.length; i++) bin += String.fromCharCode(b[i]);
    return 'data:audio/wav;base64,' + btoa(bin);
  }
  /// Перший жест на сторінці: беззвучний play() тиші на спільному елементі, одразу пауза й назад гучно. Якщо цим самим
  /// жестом людина тисне ▶ у Лавці, playAnthem уже поміняв src — тоді нічого не чіпаємо (і його пауза — не наша).
  function unlockAnthem() {
    const el = anthemEl;
    if (!el || anth) return;
    const src = silentWav();
    const mine = () => !anth && el.getAttribute('src') === src;
    const done = () => {
      if (!mine()) return;
      try { el.pause(); el.removeAttribute('src'); el.load(); } catch { /* уже порожній */ }
      el.muted = false;
    };
    try {
      el.muted = true;
      el.src = src;
      const pr = el.play();
      if (pr && pr.then) pr.then(done, done); else done();
    } catch { done(); }
  }
  /// Радіо — на повзунок з усіма притишеннями (Посиденьки × гімн).
  const radioVolume = () => { audio.volume = posToVol(+vol.value) * duckBy * duckAnthem; };
  /// a — { url, title?, emoji?, nick? }; opt.preview — людина тисне ▶ сама (вимикач гімнів тоді не діє),
  /// opt.from/opt.len — грати лише шматок (прослуховування свого файла до завантаження; дзвінок — перші 4 с),
  /// opt.fade — шматок у кінці м'яко згасає (дзвінок), opt.ring — це дзвінок заклику: він не перебиває того, що звучить,
  /// а гімн чи прокльон столу (не ▶) перебиває його самого, opt.onStart — браузер справді
  /// пустив звук (обіцянка play() виконалась; до того смужку за столом не показують), opt.onEnd — коли замовкло (і коли
  /// браузер відмовив — тоді без onStart). Вертає, чи пробує грати.
  function playAnthem(a, opt) {
    opt = opt || {};
    if (!a || !a.url) return false;
    // Гімн і прокльон столу не чекають, поки доспіває чужий дзвінок: 4 с дзвінка менш важливі за кінець партії
    const busy = anth && !(anth.ring && !opt.ring);
    if (!opt.preview && (!anthemSound() || busy)) return false;
    stopAnthem();
    const el = anthemEl = anthemEl || new Audio();
    const cur = anth = { a, preview: !!opt.preview, ring: !!opt.ring && !opt.preview, onEnd: opt.onEnd || null, timer: 0 };
    const started = () => {
      if (anth !== cur) return;
      if (opt.fade && len > 0) fadeOut(cur, el, len);
      if (!opt.onStart) return;
      try { opt.onStart(); } catch (e) { console.warn('[anthem] onStart', e); }
    };
    const end = () => finishAnthem(cur);
    const from = Math.max(0, +opt.from || 0);
    const len = Math.max(0, +opt.len || 0);
    const p = +vol.value;
    try { el.volume = posToVol(p); } catch { /* iOS: volume лише для читання — там лишається muted */ }
    const mute = p === 0;
    // З середини файла: мовчимо, поки не перескочили на «звідки», — інакше на мить чути початок пісні.
    el.muted = mute || from > 0;
    el.onloadedmetadata = from > 0 ? () => { try { el.currentTime = from; } catch { el.muted = mute; } } : null;
    el.onseeked = from > 0 ? () => { el.muted = mute; el.onseeked = null; } : null;
    el.ontimeupdate = len > 0 ? () => { if (el.currentTime >= from + len) end(); } : null;
    el.onended = end;
    el.onerror = end;
    // pause від попереднього гімну приходить подією вже після нового play() — тоді el.paused хибне, і це не нам
    el.onpause = () => { if (el.paused) end(); };
    cur.timer = setTimeout(end, Math.max(ANTHEM_MAX_MS, (len + 4) * 1000));
    el.preload = 'auto';
    el.src = a.url;
    duckAnthem = ANTHEM_DUCK;
    radioVolume();
    paintAnthemBtns();
    let pr = null;
    try { pr = el.play(); } catch { end(); return false; }
    // браузер не дав звуку (жесту на сторінці ще не було) — тихо відступаємо, радіо повертаємо; дав — onStart
    if (pr && pr.then) pr.then(started, end); else started();
    return true;
  }
  /// 🔔 Дзвінок (flair.md §2): останні ANTHEM_FADE с шматка — гучність плавно вниз, а не обрив. Рахуємо від справжнього
  /// старту (onStart), а не від play(): мережа могла тягнути файл. iOS гучність не дає — там просто зупиниться в кінці.
  const ANTHEM_FADE = 0.8;
  function fadeOut(cur, el, len) {
    const v0 = el.volume;
    cur.fadeT = setTimeout(() => {
      const t0 = Date.now();
      cur.fadeI = setInterval(() => {
        if (anth !== cur) { clearInterval(cur.fadeI); return; }
        const k = Math.max(0, 1 - (Date.now() - t0) / (ANTHEM_FADE * 1000));
        try { el.volume = v0 * k; } catch { /* iOS: лише читання */ }
        if (k <= 0) clearInterval(cur.fadeI);
      }, 50);
    }, Math.max(0, (len - ANTHEM_FADE) * 1000));
  }
  function finishAnthem(cur) {
    if (!cur || anth !== cur) return;
    anth = null;
    clearTimeout(cur.timer);
    clearTimeout(cur.fadeT);
    clearInterval(cur.fadeI);
    const el = anthemEl;
    if (el) {
      el.onended = el.onerror = el.onpause = el.ontimeupdate = el.onloadedmetadata = el.onseeked = null;
      try { el.pause(); el.removeAttribute('src'); el.load(); } catch { /* уже порожній */ }
    }
    duckAnthem = 1;
    radioVolume();
    paintAnthemBtns();
    if (cur.onEnd) { try { cur.onEnd(); } catch (e) { console.warn('[anthem] onEnd', e); } }
  }
  function stopAnthem() { if (anth) finishAnthem(anth); }
  /// Що звучить зараз: { url, title, … } або null.
  const anthemPlaying = () => (anth ? anth.a : null);
  /// ▶ прослухати / ⏹ зупинити — на одній кнопці (Лавка, профіль, адмінський список).
  function toggleAnthem(a, opt) {
    if (anth && a && anth.a.url === a.url) { stopAnthem(); return false; }
    return playAnthem(a, Object.assign({}, opt, { preview: true }));
  }
  /// Кнопки з data-anth="<url>": ▶, поки цей гімн мовчить, ⏹ — поки грає. data-lbl — підпис після значка.
  function paintAnthemBtns(scope) {
    const url = anth ? anth.a.url : null;
    (scope || document).querySelectorAll('button[data-anth]').forEach((b) => {
      const on = !!url && b.dataset.anth === url;
      const lbl = b.dataset.lbl || '';
      b.textContent = on ? '⏹' + (lbl ? ' Стоп' : '') : '▶' + (lbl ? ' ' + lbl : '');
      b.setAttribute('aria-pressed', String(on));
      b.classList.toggle('on', on);
    });
  }
  /// Кнопка до столу біля рядка балачок: «Сісти», поки є куди, інакше «Дивитись». Столу вже нема —
  /// кнопки теж нема: мертве посилання гірше, ніж його відсутність. Що там за стіл, знає HGames.
  /// <c>named</c> — чи назвати гру на самій кнопці; у рядку Журналу вона вже названа в тексті.
  function roomBtn(id, named) {
    const link = window.HGames && HGames.roomLink && HGames.roomLink(id);
    if (!link) return null;
    const b = document.createElement('button');
    b.className = 'roomlink' + (link.canSit ? ' primary' : '');
    b.title = link.who;
    const paint = () => {
      const now = HGames.roomLink(id) || link;
      b.innerHTML = (named ? now.title : now.icon) + '<span class="rl-go">' + esc(now.label) + '</span>';
    };
    paint();
    // Іконка гри приходить із її модулем, а він міг ще не завантажитись: домалюємо, щойно прилетить.
    if (HGames.ensureIcon) HGames.ensureIcon(link.game, paint);
    b.onclick = () => ((HGames.roomLink(id) || link).canSit
      ? HGames.sitAt(id, b)          // питання «встати з попереднього столу?» має бути видно до того, як кнопка закрутиться
      : HGames.openAt(id));
    return b;
  }
  /// Кнопки до столів у вже намальованих рядках. Слот у рядку стоїть завжди, кнопка в ньому —
  /// поки стіл живий: стіл заповнився чи його прибрали — рядок міняється разом із ним, а не бреше.
  function paintRoomSlots(root) {
    for (const slot of (root || document).querySelectorAll('.roomslot')) {
      const b = roomBtn(slot.dataset.room, slot.dataset.named === '1');
      slot.textContent = '';
      if (b) slot.appendChild(b);
      // Рядок-заклик живе, поки за стіл можна сісти (або це твій стіл): заповнився чи зник — рядок ховається.
      const row = slot.closest('.msg.invite');
      if (row) {
        const link = window.HGames && HGames.roomLink(slot.dataset.room);
        row.hidden = !link || !(link.canSit || link.mine);
      }
    }
  }
  /// Тіло рядка: нік, текст, кубик чи монетка, Глек з аватаркою, рядок Журналу. Спільне для Балачок і балачки
  /// столу — рядки столу мають ті самі поля (nick, text, kind, at), лише без лайків і відповідей.
  /// Одна кісточка чи монетка для рядка кидків. Кілька кидків одного ніка поспіль стають одним рядком
  /// (rollInto) — раніше дванадцять /кубик займали пів панелі.
  function rollEl(m, live) {
    const s = document.createElement('span');
    if (m.kind === 'coin') {
      // Бік читаємо хвостом рядка, а не першим словом: боків колись може стати більше («Стало ребром»),
      // і двослівний не має лишити порожню плитку. Формат не впізнали — малюємо текст як є.
      const hit = /🪙\s+(.+)$/.exec(m.text || '');
      const side = hit ? hit[1].trim() : String(m.text || '').replace('🪙', '').trim();
      s.className = 'die coin';
      s.innerHTML = '<i class="cf">🪙</i><b class="cs"></b>';
      s.querySelector('.cs').textContent = side;
      if (live) flipCoin(s.querySelector('.cf'), s.querySelector('.cs'));
      return s;
    }
    const [, value, min, max] = /🎲 (\d+) \((\d+)–(\d+)\)/.exec(m.text) || [];
    const [v, lo, hi] = [+value, +min, +max];
    s.className = 'die' + (isFace(lo, hi) ? ' face' : '');
    s.dataset.rng = lo + '–' + hi;
    s.title = 'з ' + lo + '–' + hi;
    paintDie(s, v, lo, hi);
    if (live) rollDie(s, lo, hi, v);
    return s;
  }
  /// Під рядком кидків — межі, якщо вони в усіх однакові («з 1–6»); різні — видно в підказці кожної кісточки.
  function paintRollRange(el) {
    const rngs = [...new Set([...el.querySelectorAll('.dies .die[data-rng]')].map((d) => d.dataset.rng))];
    const coins = el.querySelectorAll('.dies .die.coin').length;
    el.querySelector('.rng').textContent = rngs.length === 1 && !coins ? 'з ' + rngs[0] : '';
  }
  const ROLL_MS = 3 * 60e3;
  const ROLL_MAX = 12;
  /// Той самий нік кидає ще раз невдовзі — дописуємо кісточку в його рядок, а не заводимо новий.
  const rollable = (prev, m) => !!prev && prev.classList.contains('dice') && (m.kind === 'dice' || m.kind === 'coin')
    && sameNick(prev.dataset.nick, m.nick) && prev.dataset.day === dayKey(m.at)
    && new Date(m.at).getTime() - +prev.dataset.ts < ROLL_MS && prev.querySelectorAll('.dies > .die').length < ROLL_MAX;
  function rollInto(prev, m, live) {
    prev.querySelector('.dies').appendChild(rollEl(m, live));
    paintRollRange(prev);
    prev.querySelector('.time').textContent = tm(m.at);
    prev.dataset.ts = String(new Date(m.at).getTime());
  }

  /// Що зроблено в Лавці — рядком у балачках: «🎁 Оля дарує Петрові …», «💌 Оля присвячує …», «🎆 Оля запускає феєрверк!».
  const DEEDS = { gift: '🎁', dedication: '💌', fx: '🎆' };
  function fillMessage(el, m, mine, live) {
    const isLog = m.kind === 'system';
    // Монетка живе в тій самій розкладці, що й кубик (.msg.dice — рядок у флексі); /choose і /8ball
    // це звичайні рядки з іконкою в самому тексті, тож їм окрема гілка ні до чого.
    el.className = 'msg ' + (isLog ? 'system' : m.kind === 'dj' || m.kind === 'padel' ? 'dj'
      : m.kind === 'dice' || m.kind === 'coin' ? 'dice'
        : m.kind === 'tables' ? 'tables' : m.kind === 'invite' ? 'invite' : m.kind === 'note' ? 'note'
          : m.kind === 'mod' ? 'modline' : DEEDS[m.kind] ? 'deed ' + m.kind : mine ? 'mine' : '');
    if (DEEDS[m.kind]) {
      // Подарували чи присвятили мені — підсвічуємо, як особистий заклик.
      if (m.to && sameNick(m.to, me.nick)) el.classList.add('tome');
      el.innerHTML = `<span class="iv-ico">${DEEDS[m.kind]}</span>${nickHtml(m.nick, 'n', true)}`
        + `<span class="t">${esc(String(m.text || '').replace(/^\s*(🎁|💌|🎆)\s*/u, ''))}</span><span class="time">${tm(m.at)}</span>`;
      if (live && m.kind === 'fx') el.classList.add('flash');
    } else if (m.kind === 'invite') {
      // «📣 Влад кличе в Мафію [Сісти]» — живий рядок: кнопку домальовує paintRoomSlots, а щойно сісти вже нікуди,
      // рядок ховається сам. Особистий заклик (personal) бачить лише той, кого кликали, — його й підсвічуємо.
      if (m.personal) el.classList.add('tome');
      el.innerHTML = `<span class="iv-ico">📣</span>${nickHtml(m.nick, 'n', true)}<span class="t">${esc(m.text)}</span><span class="time">${tm(m.at)}</span>`;
    } else if (m.kind === 'mod') {
      // Адмін когось обмежив чи щось перемкнув на всі Балачки (ChatModeration.cs): рядок від сайту, без ніка.
      el.innerHTML = `<span class="t">${esc(m.text)}</span><span class="time">${tm(m.at)}</span>`;
    } else if (m.kind === 'note') {
      // Особиста відповідь сервера («📣 Покликав Олю») — як /столи: бачиш лише ти, у базі її нема.
      el.innerHTML = `<span class="t">${linkify(m.text)}</span><span class="time">лише тобі</span>`;
    } else if (m.kind === 'tables') {
      // Відповідь на /столи бачить лише той, хто спитав: це погляд у лобі, не виходячи з балачок,
      // а не репліка. Тому вона й у базу не лягає — після F5 її не буде, і це правильно.
      el.innerHTML = '<span class="n">🎲 ' + esc(m.text) + '</span><span class="time">лише тобі</span>'
        + '<div class="tlist">'
        + (m.rooms || []).map((id) => '<span class="roomslot" data-named="1" data-room="' + esc(id) + '"></span>').join('')
        + '</div>';
      paintRoomSlots(el);
      if (!el.querySelector('.roomlink')) el.querySelector('.tlist').innerHTML = '<span class="muted small">Столи щойно розібрали.</span>';
    } else if (m.kind === 'coin' || m.kind === 'dice') {
      el.classList.toggle('mine', mine);
      el.innerHTML = `${nickHtml(m.nick, 'n', true)}<span class="dies"></span><span class="rng muted small"></span><span class="time">${tm(m.at)}</span>`;
      el.querySelector('.dies').appendChild(rollEl(m, live));
      paintRollRange(el);
    } else if (m.kind === 'dj' || m.kind === 'padel') {
      // Падельня — рядок Глека про матч: ім'я Глека, як у dj-рядків, а не людини з таким ніком
      el.innerHTML = `<img src="/static/glek.svg" alt=""><div><span class="n">${esc(m.kind === 'padel' ? dj() : m.nick)}</span>${linkify(m.text)}<span class="time">${tm(m.at)}</span></div>`;
    } else if (isLog) {
      el.innerHTML = `<span class="time">${tm(m.at)}</span>${linkify(m.text)}`;
    } else {
      // Саме лише «🔥» — не рядок тексту, а жест: показуємо на весь зріст, поки їх не набралося багато.
      const big = emojiCount(m.text);
      if (big && big <= 3) el.classList.add('big');
      // корона — всередині ніка: repaintCrowns() переставляє її саме там
      el.innerHTML = `<span class="n who-n${HPeople.nickCls(m.nick)}" data-who="${esc(m.nick)}" data-nb="1" style="--h:${HPeople.hue(m.nick)}">${HPeople.badge(m.nick)}${esc(m.nick)}${crownOf(m.nick)}</span>`
        + (m.file ? fileHtml(m.file) : '')
        + `<span class="t">${highlightMentions(linkify(m.text))}</span><span class="time">${tm(m.at)}</span>`;
      if (m.file) el.dataset.fname = m.file.name;
    }
    // Для днів, групування й гортання вгору: коли, хто і який це рядок у базі.
    el.dataset.day = dayKey(m.at);
    el.dataset.ts = String(new Date(m.at).getTime());
    el.dataset.nick = m.nick || '';
    el.dataset.grp = m.kind === 'chat' && !m.replyTo ? '1' : '';
  }

  // ---------- файли в Балачках (ChatFiles.cs) ----------
  const FILE_MAX = 32 * 1024 * 1024;
  const FILE_ICONS = [[/\.(zip|rar|7z|tar|gz)$/i, '🗜'], [/\.pdf$/i, '📕'], [/\.(docx?|odt|rtf|txt|md)$/i, '📝'],
    [/\.(xlsx?|ods|csv)$/i, '📊'], [/\.(pptx?|odp)$/i, '📽'], [/\.(exe|msi|apk)$/i, '⚙'], [/\.(mp3|ogg|wav|flac|m4a)$/i, '🎵'],
    [/\.(mp4|webm|mov|mkv|avi)$/i, '🎬'], [/\.(jpe?g|png|gif|webp|heic|avif|svg)$/i, '🖼']];
  const fileSize = (b) => b < 1024 ? b + ' Б' : b < 1048576 ? Math.round(b / 1024) + ' КБ'
    : (b / 1048576).toFixed(b < 10 * 1048576 ? 1 : 0).replace('.', ',') + ' МБ';
  const fileIcon = (name) => (FILE_ICONS.find(([re]) => re.test(name)) || [0, '📄'])[1];
  /// Картинка — прямо в рядку (розміри з сервера тримають місце, поки вантажиться), відео й звук — програвачем,
  /// решта — карткою «📄 ім'я · розмір» на скачування.
  function fileHtml(f) {
    const url = esc(f.url), name = esc(f.name);
    const dl = `<a class="mfile-dl" href="${url}" download="${name}" title="Скачати"><span>⬇</span><span class="fn">${name}</span><span class="fs">${fileSize(f.size)}</span></a>`;
    if (f.type === 'image') {
      // не ширше 360 і не вище 320 px, пропорції — з width/height (рядок не стрибає, поки картинка вантажиться)
      const wh = f.w && f.h ? ` width="${+f.w}" height="${+f.h}" style="width:${Math.max(40, Math.round(Math.min(360, f.w, f.w * 320 / f.h)))}px"` : '';
      return `<a class="mfile img" href="${url}" target="_blank" rel="noopener" title="${name}"><img src="${url}" alt="${name}" loading="lazy" decoding="async"${wh}></a>`;
    }
    if (f.type === 'video') return `<video class="mfile vid" src="${url}" controls preload="metadata" playsinline></video>${dl}`;
    if (f.type === 'audio') return `<audio class="mfile aud" src="${url}" controls preload="none"></audio>${dl}`;
    return `<a class="mfile doc" href="${url}" download="${name}" title="Скачати"><span class="ico">${fileIcon(f.name)}</span><span class="fn">${name}</span><span class="fs">${fileSize(f.size)}</span></a>`;
  }

  /// Готовий рядок Балачок чи Журналу — ще не вставлений у скриньку.
  function buildMessage(m, live) {
    const isLog = m.kind === 'system';
    const el = document.createElement('div');
    fillMessage(el, m, sameNick(m.nick, me.nick), live);
    if (!isLog && m.kind !== 'tables' && m.kind !== 'invite' && m.kind !== 'mod' && m.id > 0) decorateMessage(el, m);
    // Рядок про живий стіл («Новий стіл: Мафія», «Оля і Петро сіли грати») носить його id — лишаємо
    // слот під кнопку, щоб до столу можна було дійти прямо звідси (PLAN.md §7.4).
    if (m.roomId && m.kind !== 'tables') {
      const slot = document.createElement('span');
      slot.className = 'roomslot';
      slot.dataset.room = m.roomId;
      el.appendChild(slot);
      paintRoomSlots(el);
    }
    el.dataset.mid = String(m.id || 0);
    if (isLog) {
      el.dataset.topic = m.topic || (m.roomId ? 'games' : 'radio');
      el.dataset.text = m.text || '';
      el.dataset.at = m.at;
      const act = logAct(m);
      if (act) el.dataset.act = act.key;
    }
    return el;
  }

  function addMessage(m, scroll = true, live = false) {
    const isLog = m.kind === 'system';
    const box = isLog ? $('log') : $('messages');
    const mine = sameNick(m.nick, me.nick);
    // Хто гортає історію вгору, того донизу не тягнемо: стрибок посеред читання гірший за «нове внизу».
    const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    const prev = lastMsg(box);
    if (!isLog && rollable(prev, m)) {
      // ще один кидок того самого ніка — у його рядок, а не новим рядком
      rollInto(prev, m, live);
      if (scroll && (atBottom || mine)) box.scrollTop = box.scrollHeight;
      if (live) typingGone('chat', m.nick);
      return;
    }
    const el = buildMessage(m, live);
    if (!(isLog && coalesce(prev, el, m))) {
      if (!prev || prev.dataset.day !== el.dataset.day) box.appendChild(daySep(m.at));
      else if (!isLog && groupable(prev, el)) el.classList.add('cont');
      box.appendChild(el);
    }
    if (isLog) tidyDays(box);
    if (!scroll || atBottom) trimTop(box, KEEP_LINES);
    if (scroll && (atBottom || mine)) box.scrollTop = box.scrollHeight;
    if (!isLog) learnNick(m.nick);
    // Порожнє з порожнім теж «збігається» — без цього той, хто ще не назвався, бачив би кожен рядок як відповідь собі.
    const repliedMe = !mine && !isLog && !!me.nick && !!m.replyNick && sameNick(m.replyNick, me.nick);
    const tagged = !mine && !isLog && m.kind === 'chat' && taggedMe(m.text);
    if (repliedMe || tagged) el.classList.add('tome');
    // звук — лише на живе повідомлення, не на історію після F5
    if (live && (repliedMe || tagged)) ping();
    if (live && !isLog) typingGone('chat', m.nick);
    if (!isLog && scroll && !mine && !chatVisible()) {
      // Непрочитане — це люди. Глек бейджа не смикає: інакше той не сходив би з екрана й нічого б не означав.
      if (m.kind !== 'dj') setUnread(unread + 1);
      if (repliedMe) toast(`↩ ${m.nick} відповідає тобі: ${m.text}`.slice(0, 140));
      else if (tagged) toast(`@ ${m.nick} гукає тебе: ${m.text}`.slice(0, 140));
      else if (mentionsMe(m.text)) toast(`${m.nick}: ${m.text}`.slice(0, 140));
    }
  }

  // ---------- історія: дні, один нік на кілька реплік, гортання вгору ----------
  const MONTHS = ['січня', 'лютого', 'березня', 'квітня', 'травня', 'червня', 'липня', 'серпня', 'вересня', 'жовтня', 'листопада', 'грудня'];
  const dayKey = (at) => { const d = new Date(at); return d.getFullYear() + '-' + d.getMonth() + '-' + d.getDate(); };
  /// «Сьогодні», «Вчора», «19 вересня» (рік — лише коли не цьогорічне).
  function dayLabel(at) {
    const d = new Date(at), now = new Date();
    const start = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const days = Math.round((start(now) - start(d)) / 86400000);
    if (days === 0) return 'Сьогодні';
    if (days === 1) return 'Вчора';
    return d.getDate() + ' ' + MONTHS[d.getMonth()] + (d.getFullYear() !== now.getFullYear() ? ' ' + d.getFullYear() : '');
  }
  function daySep(at) {
    const s = document.createElement('div');
    s.className = 'msg-day';
    s.dataset.day = dayKey(at);
    s.dataset.at = at;
    s.innerHTML = '<span>' + esc(dayLabel(at)) + '</span>';
    return s;
  }
  // Опівночі «Сьогодні» стає «Вчора»: щохвилини дивимось, чи не змінилась дата, і тоді перечитуємо підписи.
  let labelsDay = new Date().toDateString();
  setInterval(() => {
    const today = new Date().toDateString();
    if (today === labelsDay) return;
    labelsDay = today;
    document.querySelectorAll('.msg-day').forEach((s) => { s.firstElementChild.textContent = dayLabel(s.dataset.at); });
  }, 60e3);

  const msgs = (box) => box.querySelectorAll(':scope > .msg');
  const lastMsg = (box) => { const all = msgs(box); return all.length ? all[all.length - 1] : null; };
  const firstMsg = (box) => box.querySelector(':scope > .msg');
  /// Кілька реплік поспіль від одного — під одним ніком: той самий день, до п'яти хвилин між ними, без цитати.
  const GROUP_MS = 5 * 60e3;
  const groupable = (prev, el) => !!prev && prev.dataset.grp === '1' && el.dataset.grp === '1'
    && sameNick(prev.dataset.nick, el.dataset.nick) && prev.dataset.day === el.dataset.day
    && +el.dataset.ts - +prev.dataset.ts < GROUP_MS;

  /// Скільки рядків тримати в скриньці, поки людина внизу. Нагорі — хоч скільки: вона ж сама їх підвантажила.
  const KEEP_LINES = 400;
  function trimTop(box, max) {
    let drop = msgs(box).length - max;
    if (drop <= 0) return;
    while (drop > 0 && box.firstElementChild) {
      const x = box.firstElementChild;
      if (x.classList.contains('msg')) drop--;
      x.remove();
    }
    // Голова скриньки знову мусить починатись із дня, а перший рядок — із ніка.
    const head = firstMsg(box);
    if (head) {
      head.classList.remove('cont');
      if (!box.firstElementChild.classList.contains('msg-day')) box.insertBefore(daySep(new Date(+head.dataset.ts).toISOString()), box.firstElementChild);
    }
    delete box.dataset.done;   // згори знову є що підвантажити
  }

  /// Скільки старшого просити за раз — стільки ж віддає сервер (RadioHub.ChatBefore).
  const OLDER = 60;
  async function loadOlder(box, depth = 0) {
    if (!conn || box._loading || box.dataset.done === '1' || box.hidden) return;
    const first = box.querySelector(':scope > .msg:not([data-mid="0"])');
    const before = first ? +first.dataset.mid : 0;
    if (!before) return;
    box._loading = true;
    let list;
    try { list = await conn.invoke('ChatBefore', before, box.id === 'log'); }
    catch { list = null; }   // зв'язку нема — спробуємо, коли гортатимуть знову
    finally { box._loading = false; }
    // null — сервер попросив не так часто: це ще не кінець історії, просто не зараз.
    if (list == null) return;
    if (list.length) prependOlder(box, list);
    if (list.length < OLDER) { markTop(box); return; }
    // Під фільтром Журналу пачка могла не додати жодного видимого рядка, а короткий список — не дати смуги
    // прокрутки: нової події scroll тоді не буде. Беремо ще кілька пачок, поки скринька не заповниться.
    if (depth < 5 && box.offsetParent && (box.scrollTop < 80 || box.scrollHeight <= box.clientHeight + 40)) await loadOlder(box, depth + 1);
  }
  /// Скринька, яку нема чим гортати (фільтр сховав майже все), сама просить старіше.
  function fillUp(box) {
    if (box.offsetParent && box.scrollHeight <= box.clientHeight + 40) loadOlder(box);
  }
  function markTop(box) {
    box.dataset.done = '1';
    if (box.querySelector(':scope > .msg-top')) return;
    const t = document.createElement('div');
    t.className = 'msg-top muted small';
    t.textContent = box.id === 'log' ? 'Далі Журнал не пам\'ятає' : 'Це найперше, що тут тяпнули';
    box.insertBefore(t, box.firstChild);
  }
  function prependOlder(box, list) {
    const log = box.id === 'log';
    const frag = document.createDocumentFragment();
    let prev = null;
    for (const m of list) {
      if (!log && rollable(prev, m)) { rollInto(prev, m, false); continue; }
      const el = buildMessage(m, false);
      if (log && coalesce(prev, el, m)) continue;
      if (!prev || prev.dataset.day !== el.dataset.day) frag.appendChild(daySep(m.at));
      else if (!log && groupable(prev, el)) el.classList.add('cont');
      frag.appendChild(el);
      prev = el;
      if (!log) learnNick(m.nick);
    }
    // Шов зі старою головою: той самий день — її роздільник уже зайвий, а той самий автор — без ніка.
    const head = firstMsg(box);
    if (prev && head && prev.dataset.day === head.dataset.day) {
      const sep = head.previousElementSibling;
      if (sep && sep.classList.contains('msg-day')) sep.remove();
      if (!log && groupable(prev, head)) head.classList.add('cont');
    }
    const h0 = box.scrollHeight, t0 = box.scrollTop;
    box.insertBefore(frag, box.firstChild);
    box.scrollTop = t0 + (box.scrollHeight - h0);   // те, що людина читала, лишається на місці
    if (log) tidyDays(box);
  }
  for (const id of ['messages', 'log']) $(id).addEventListener('scroll', (e) => { if (e.target.scrollTop < 80) loadOlder(e.target); });

  // ---------- Журнал: «📻 Радіо · 🎮 Ігри» і склеювання однакового ----------
  let logFilter = (() => { try { return localStorage.getItem('logFilter') || 'all'; } catch { return 'all'; } })();
  function setLogFilter(f) {
    logFilter = ['radio', 'games'].includes(f) ? f : 'all';
    try { localStorage.setItem('logFilter', logFilter); } catch { /* приватне вікно */ }
    const box = $('log');
    box.classList.toggle('lf-radio', logFilter === 'radio');
    box.classList.toggle('lf-games', logFilter === 'games');
    $('logFilters').querySelectorAll('[data-lf]').forEach((b) => b.classList.toggle('on', b.dataset.lf === logFilter));
    tidyDays(box);
    box.scrollTop = box.scrollHeight;
    fillUp(box);
  }
  $('logFilters').querySelectorAll('[data-lf]').forEach((b) => b.onclick = () => setLogFilter(b.dataset.lf));
  /// Роздільник дня, під яким під фільтром не лишилось жодного рядка, — зайвий.
  function tidyDays(box) {
    if (box.id !== 'log') return;
    const shown = (x) => logFilter === 'all' || x.dataset.topic === logFilter;
    let sep = null, any = false;
    for (const x of box.children) {
      if (x.classList.contains('msg-day')) { if (sep) sep.hidden = !any; sep = x; any = false; }
      else if (x.classList.contains('msg') && shown(x)) any = true;
    }
    if (sep) sep.hidden = !any;
  }

  /// Хто, що і з чим: «Smaug скіпає X» → { who: Smaug, verb: скіпає, what: X }. Лише дії радіо — їх буває підряд багато.
  const LOG_VERBS = /^(.+?) (скіпає|додає|закидає|❤|прибирає|відхиляє|банить|розбанює|викуповує|бере пораду [^:]+:) (.+)$/;
  function logAct(m) {
    const hit = LOG_VERBS.exec(m.text || '');
    return hit ? { key: hit[1].toLowerCase() + '|' + hit[2], who: hit[1], verb: hit[2], what: hit[3] } : null;
  }
  /// Та сама дія тієї самої людини поспіль (у межах 15 хвилин і того самого дня) — одним рядком «Smaug скіпає ×4».
  const COALESCE_MS = 15 * 60e3;
  function coalesce(prev, el, m) {
    if (!prev || !el.dataset.act || prev.dataset.act !== el.dataset.act || prev.dataset.day !== el.dataset.day) return false;
    if (+el.dataset.ts - +prev.dataset.ts > COALESCE_MS) return false;
    const act = logAct(m);
    if (!prev._items) {
      const first = logAct({ text: prev.dataset.text });
      prev._items = [{ at: prev.dataset.at, what: first ? first.what : prev.dataset.text }];
    }
    prev._items.push({ at: m.at, what: act.what });
    prev.dataset.ts = el.dataset.ts;
    paintGroup(prev, act);
    return true;
  }
  function paintGroup(el, act) {
    const items = el._items;
    const open = el.classList.contains('open');
    el.classList.add('lgroup');
    el.innerHTML = `<span class="time">${tm(items[items.length - 1].at)}</span>${linkify(act.who + ' ' + act.verb)} <b class="lg-n">×${items.length}</b>`
      + ` <button type="button" class="ghost lg-more" aria-expanded="${open}">${open ? 'сховати' : 'що саме'}</button>`
      + `<div class="lg-items"${open ? '' : ' hidden'}>${items.map((x) => `<div><span class="time">${tm(x.at)}</span>${linkify(x.what)}</div>`).join('')}</div>`;
  }
  $('log').addEventListener('click', (e) => {
    const b = e.target.closest('.lg-more');
    if (!b) return;
    const row = b.closest('.msg');
    row.classList.toggle('open');
    const open = row.classList.contains('open');
    row.querySelector('.lg-items').hidden = !open;
    b.textContent = open ? 'сховати' : 'що саме';
    b.setAttribute('aria-expanded', String(open));
  });

  // ---------- «Оля тяпає…» ----------
  const TYPING_MS = 6000;
  const typers = { chat: new Map(), table: new Map() };   // нік → коли забути
  const typedAt = { chat: 0, table: 0 };
  function typingSeen(t) {
    if (!t || !t.nick || sameNick(t.nick, me.nick)) return;
    const scope = t.room ? (t.room === table.id ? 'table' : null) : 'chat';
    if (!scope) return;
    typers[scope].set(t.nick, Date.now() + TYPING_MS);
    paintTyping();
  }
  function typingGone(scope, nick) { if (typers[scope].delete(nick)) paintTyping(); }
  function typingText(map) {
    const now = Date.now();
    for (const [n, until] of map) if (until < now) map.delete(n);
    const names = [...map.keys()];
    if (!names.length) return '';
    if (names.length === 1) return names[0] + ' тяпає…';
    if (names.length === 2) return names[0] + ' і ' + names[1] + ' тяпають…';
    return names[0] + ', ' + names[1] + ' і ще ' + (names.length - 2) + ' тяпають…';
  }
  function paintTyping() {
    const a = typingText(typers.chat), b = typingText(typers.table);
    $('typingLine').textContent = a;
    $('typingLine').hidden = !a || chatTab !== 'chat';
    tc.typing.textContent = b;
    tc.typing.hidden = !b;
  }
  setInterval(() => { if (typers.chat.size || typers.table.size) paintTyping(); }, 1000);
  /// Сказати серверу «пишу» — не частіше ніж раз на 2,5 с; на команди (/кубик) не кажемо.
  function sendTyping(scope, text) {
    const now = Date.now();
    if (!conn || !text.trim() || text.startsWith('/') || now - typedAt[scope] < 2500) return;
    typedAt[scope] = now;
    conn.invoke('Typing', scope === 'table' ? table.id : null).catch(() => {});
  }
  $('chatInput').addEventListener('input', () => sendTyping('chat', $('chatInput').value));

  // ---------- балачка столу ----------
  // Стіл, біля якого ти стоїш, має свою розмову: гравці, глядачі й Глек-ведучий. У загальні Балачки з гри
  // не йде нічого. На широкому екрані розмова — вкладка «🎲 Стіл» у панелі; на телефоні, у ⛶ і коли панель
  // згорнута — шторка знизу. Сам стіл і його зміни каже каркас ігор (HGames.init → onTable).
  // opened — для якого столу балачку вже розгортали самі (мафія): вдруге не нав'язуємо, людина могла її згорнути.
  const table = { id: null, info: null, lines: new Map(), unread: 0, open: false, mode: null, full: false, opened: null };
  const tc = (() => {
    const root = document.createElement('div');
    root.className = 'tchat';
    root.innerHTML = '<button type="button" class="tc-head" aria-expanded="false" title="Балачка столу">'
      + '<span class="tc-ico">💬</span><span class="tc-last">Стіл</span><span class="chip badge tc-badge" hidden>0</span></button>'
      + '<div class="tc-body">'
      + '<div class="tc-top"><b class="tc-name"></b><button type="button" class="ghost icon tc-close" title="Згорнути" aria-label="Згорнути">✕</button></div>'
      + '<div class="tc-lines messages"></div>'
      + '<div class="tc-typing typing" hidden></div>'
      + '<form class="tc-form"><input type="text" maxlength="500" autocomplete="off" placeholder="Тяпни щось за столом…">'
      + '<button class="primary" type="submit" title="Тяпнути (Enter)">→</button></form></div>';
    const q = (s) => root.querySelector(s);
    const o = {
      root, head: q('.tc-head'), last: q('.tc-last'), badge: q('.tc-badge'), name: q('.tc-name'),
      lines: q('.tc-lines'), typing: q('.tc-typing'), form: q('.tc-form'), input: q('.tc-form input'),
    };
    o.head.onclick = () => setTableOpen(!table.open);
    q('.tc-close').onclick = () => setTableOpen(false);
    o.form.onsubmit = (e) => { e.preventDefault(); tableSend(); };
    o.input.addEventListener('input', () => sendTyping('table', o.input.value));
    // Esc у шторці — згорнути її, а не піти зі столу (каркас ловить Esc на рівні документа).
    o.input.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && table.mode === 'drawer') { e.stopPropagation(); setTableOpen(false); }
    });
    return o;
  })();

  /// Де зараз живе балачка столу: 'rail' — вкладка в панелі, 'drawer' — шторка, null — ми не біля столу.
  const tableMode = () => (!table.id ? null : isMobile() || table.full || !chatOpen ? 'drawer' : 'rail');
  function placeTable() {
    const mode = tableMode();
    const was = table.mode;
    table.mode = mode;
    $('tableTab').hidden = mode !== 'rail';
    tc.root.classList.toggle('rail', mode === 'rail');
    tc.root.classList.toggle('drawer', mode === 'drawer');
    if (mode === 'rail') { if (tc.root.parentElement !== $('tablePane')) $('tablePane').appendChild(tc.root); }
    else if (mode === 'drawer') { if (tc.root.parentElement !== document.body) document.body.appendChild(tc.root); }
    else tc.root.remove();
    if (mode !== 'rail' && chatTab === 'table') setChatTab('chat');
    setTableOpen(table.open, true);
    if (mode && mode !== was) scrollTable();
    paintTableBadge();
  }
  function setTableOpen(on, quiet) {
    table.open = !!on && table.mode === 'drawer';
    tc.root.classList.toggle('open', table.open);
    tc.head.setAttribute('aria-expanded', String(table.open));
    if (!table.open) return;
    table.unread = 0;
    paintTableBadge();
    scrollTable();
    // На телефоні фокус підкинув би клавіатуру на пів екрана ще до того, як людина щось вирішила.
    if (!quiet && !isMobile()) tc.input.focus();
  }
  const tableVisible = () => !document.hidden && (table.mode === 'rail' ? chatTab === 'table' && chatOpen : table.mode === 'drawer' && table.open);
  const scrollTable = () => { tc.lines.scrollTop = tc.lines.scrollHeight; };
  function paintTableBadge() {
    const n = table.unread;
    for (const b of [$('tableBadge'), tc.badge]) { b.hidden = !n; b.textContent = n; }
  }
  /// На згорнутій шторці — останнє, що сказали: видно, чи варто розгортати.
  function paintTableLast() {
    const list = (table.id && table.lines.get(table.id)) || [];
    const l = list[list.length - 1];
    tc.last.textContent = !l ? 'Стіл' : (l.kind === 'dj' ? '🏺 ' : l.nick + ': ') + l.text;
  }
  /// Розгорнути балачку столу: вкладку в панелі (розгорнувши саму панель) або шторку.
  function openTable(focus) {
    if (!table.id) return;
    if (table.mode === 'rail') {
      setChatTab('table');
      if (focus && !isMobile()) tc.input.focus();
    } else if (table.mode === 'drawer') setTableOpen(true, !focus);
  }
  function renderTableLines() {
    const box = tc.lines;
    box.innerHTML = '';
    const list = (table.id && table.lines.get(table.id)) || [];
    if (!list.length) box.innerHTML = '<div class="tc-empty muted small">Тут поки тихо. Тяпни щось першим — почують усі, хто за столом і біля нього.</div>';
    for (const l of list) appendTableLine(l, false);
    scrollTable();
    paintTableLast();
  }
  function appendTableLine(l, live) {
    const box = tc.lines;
    const empty = box.querySelector(':scope > .tc-empty');
    if (empty) empty.remove();
    const el = document.createElement('div');
    fillMessage(el, l, sameNick(l.nick, me.nick), live);
    if (l.kind === 'chat') {
      const tagged = !sameNick(l.nick, me.nick) && taggedMe(l.text);
      if (tagged) el.classList.add('tome');
    }
    if (groupable(lastMsg(box), el)) el.classList.add('cont');
    box.appendChild(el);
    while (msgs(box).length > 100) box.firstElementChild.remove();
  }
  function tableSend() {
    const text = dotToSlash(tc.input.value.trim());
    if (!text || !conn || !table.id) return;
    conn.invoke('TableSay', table.id, text)
      .then((err) => { if (err) { toast(err, 'err'); return; } tc.input.value = ''; })
      .catch((e) => toast('Халепа: не відправилось — ' + e.message, 'err'));
  }
  /// Каркас ігор каже, біля якого столу ми стоїмо (null — ні біля якого) і чи стіл на весь екран.
  function onTable(info, layout) {
    if (window.HVoice) HVoice.onTable(info);   // сів за стіл на компанію — голос іде за стіл (web/voice.js)
    table.full = !!(layout && layout.full);
    const id = info ? info.id : null;
    const changed = id !== table.id;
    table.info = info;
    table.id = id;
    if (changed) {
      table.unread = 0;
      table.open = false;
      table.opened = null;
      typers.table.clear();
      tc.input.value = '';
      renderTableLines();
    }
    tc.name.textContent = info ? info.title : '';
    placeTable();
    paintTyping();
    // Мафія — гра, де розмова і є гра: щойно підійшов до такого столу, розмова вже перед очима. Модуль гри може
    // приїхати пізніше за сам стіл (F5, посилання) — тому дивимось не на «стіл змінився», а на «для цього столу ще
    // не розгортали». На телефоні шторку не розгортаємо самі — вона закрила б картку, а «До суперечки» на ній є.
    if (info && info.main && table.opened !== id && !isMobile()) {
      table.opened = id;
      openTable(false);
    }
  }
  document.addEventListener('visibilitychange', () => { if (tableVisible()) { table.unread = 0; paintTableBadge(); } });
  window.matchMedia('(max-width: 900px)').addEventListener('change', () => placeTable());

  // ---------- ❤ і відповіді в балачках ----------
  // Кожне повідомлення людини чи Глека (не рядок Журналу) можна лайкнути й на нього відповісти. Кнопки
  // з'являються при наведенні (на телефоні — після тапу по повідомленню); подвійний клік — теж ❤.
  let replyTo = null;               // { id, nick, text } — на що зараз відповідаємо

  function decorateMessage(el, m) {
    el.dataset.id = String(m.id);
    el.dataset.nick = m.nick;
    const host = el.querySelector(':scope > div') || el;     // у Глека текст живе у внутрішньому div
    if (m.replyTo) {
      const q = document.createElement('div');
      q.className = 'rq';
      q.dataset.to = String(m.replyTo);
      q.title = 'До цього повідомлення';
      q.innerHTML = `↪ <b>${esc(m.replyNick || '')}</b> ${esc(m.replyText || '')}`;
      host.prepend(q);
    }
    const acts = document.createElement('span');
    acts.className = 'macts';
    acts.innerHTML = '<button type="button" class="ghost" data-a="like" title="❤ Вподобати (подвійний клік — теж)">❤</button>'
      + '<button type="button" class="ghost" data-a="reply" title="Відповісти">↩</button>'
      + (window.HModer ? HModer.msgActs() : '');   // адміну — 📌 і 🗑 (web/moder.js)
    el.appendChild(acts);
    const likes = document.createElement('button');
    likes.type = 'button';
    likes.className = 'mlikes';
    host.appendChild(likes);
    paintLikes(el, m.likes || []);
  }

  function paintLikes(el, likes) {
    const chip = el.querySelector('.mlikes');
    if (!chip) return;
    chip.hidden = !likes.length;
    chip.textContent = '❤ ' + likes.length;
    chip.title = likes.join(', ');
    chip.classList.toggle('on', likes.some((n) => sameNick(n, me.nick)));
    el.classList.toggle('liked', likes.some((n) => sameNick(n, me.nick)));
  }

  /// Корона переїхала — перемалювати ніки у вже намальованих повідомленнях.
  function repaintCrowns() {
    $('messages').querySelectorAll('.msg .n').forEach((n) => {
      const msg = n.closest('.msg');
      const nick = msg && msg.dataset.nick;
      if (!nick) return;
      const old = n.querySelector('.crown');
      if (old) old.remove();
      if (window.HTournament && HTournament.crowned(nick)) n.insertAdjacentHTML('beforeend', crownOf(nick));
    });
  }

  function likeMessage(id) {
    if (!conn || !id) return;
    conn.invoke('LikeChat', id).then((err) => { if (err) toast(err, 'err'); }).catch(() => {});
  }

  function setReply(el) {
    const id = +el.dataset.id;
    if (!id) return;
    const t = el.querySelector('.t') || el.querySelector(':scope > div');
    // текст без цитати й без кнопок: беремо лише саму репліку
    let text = '';
    if (t) {
      const clone = t.cloneNode(true);
      clone.querySelectorAll('.rq, .n, .time, .mlikes, .macts').forEach((x) => x.remove());
      text = clone.textContent.trim();
    }
    if (!text && el.dataset.fname) text = '📎 ' + el.dataset.fname;
    replyTo = { id, nick: el.dataset.nick || '', text };
    $('replyBar').hidden = false;
    $('replyBar').querySelector('.rb-text').innerHTML = `↩ Відповідь <b>${esc(replyTo.nick)}</b>: ${esc(text.slice(0, 80))}`;
    if (chatTab !== 'chat') setChatTab('chat');
    $('chatInput').focus();
  }

  /// Адмін прибрав репліки (ChatModeration.cs): геть з Балачок, а цитати на них — «🗑 видалене повідомлення».
  function removeMessages(ids) {
    const box = $('messages');
    for (const id of ids) {
      const el = box.querySelector(`.msg[data-id="${id}"]`);
      if (el) {
        // пішла голова групи — наступний рядок того самого ніка знову показує нік
        const next = el.nextElementSibling;
        if (next && next.classList.contains('cont') && !el.classList.contains('cont')) next.classList.remove('cont');
        const prev = el.previousElementSibling;
        el.remove();
        // день лишився без жодного рядка — геть і його підпис
        if (prev && prev.classList.contains('msg-day') && (!prev.nextElementSibling || prev.nextElementSibling.classList.contains('msg-day'))) prev.remove();
      }
      box.querySelectorAll(`.rq[data-to="${id}"]`).forEach((q) => {
        const b = q.querySelector('b');
        q.innerHTML = '↪ ' + (b ? b.outerHTML + ' ' : '') + '<i>🗑 видалене повідомлення</i>';
      });
      if (replyTo && replyTo.id === id) clearReply();
    }
  }

  function clearReply() {
    replyTo = null;
    $('replyBar').hidden = true;
  }

  $('replyCancel').onclick = () => { clearReply(); $('chatInput').focus(); };

  // ---------- @: підказка ніків ----------
  // Набираєш «@о» — випадає список тих, кого сайт знає (спершу онлайн). ↑↓ — вибір, Enter/Tab — вставити, Esc — закрити.
  let mentionSel = 0;
  function mentionQuery() {
    const inp = $('chatInput');
    const upto = inp.value.slice(0, inp.selectionStart ?? inp.value.length);
    const hit = /(^|\s)@([^\s@]*)$/.exec(upto);
    return hit ? { q: hit[2].toLowerCase(), start: upto.length - hit[2].length - 1 } : null;
  }
  function mentionList(q) {
    const online = new Set(((state && state.online) || []).map((n) => n.toLowerCase()));
    return [...knownNicks.values()]
      .filter((n) => !sameNick(n, me.nick) && n.toLowerCase().startsWith(q))
      .sort((a, b) => (online.has(b.toLowerCase()) - online.has(a.toLowerCase())) || a.localeCompare(b, 'uk'))
      .slice(0, 6);
  }
  function hideMentions() { $('mentionPick').hidden = true; }
  function showMentions() {
    const m = mentionQuery();
    const box = $('mentionPick');
    const list = m ? mentionList(m.q) : [];
    if (!list.length) { hideMentions(); return; }
    mentionSel = Math.min(mentionSel, list.length - 1);
    const online = new Set(((state && state.online) || []).map((n) => n.toLowerCase()));
    box.innerHTML = list.map((n, i) => `<button type="button" class="${i === mentionSel ? 'on' : ''}" data-nick="${esc(n)}">`
      + `@${esc(n)}${crownOf(n)}${online.has(n.toLowerCase()) ? ' <span class="dot" title="тусить на сайті"></span>' : ''}</button>`).join('');
    box.hidden = false;
    box.querySelectorAll('button').forEach((b) => b.onmousedown = (e) => { e.preventDefault(); insertMention(b.dataset.nick); });
  }
  function insertMention(nick) {
    const m = mentionQuery();
    const inp = $('chatInput');
    if (!m) return;
    const caret = inp.selectionStart ?? inp.value.length;
    inp.value = inp.value.slice(0, m.start) + '@' + nick + ' ' + inp.value.slice(caret);
    const pos = m.start + nick.length + 2;
    inp.setSelectionRange(pos, pos);
    hideMentions();
    inp.focus();
  }
  $('chatInput').addEventListener('input', () => { mentionSel = 0; showMentions(); });
  $('chatInput').addEventListener('click', showMentions);
  $('chatInput').addEventListener('blur', () => setTimeout(hideMentions, 150));
  $('chatInput').addEventListener('keydown', (e) => {
    if ($('mentionPick').hidden) return;
    const buttons = [...$('mentionPick').querySelectorAll('button')];
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      mentionSel = (mentionSel + (e.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
      showMentions();
    } else if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault();
      e.stopImmediatePropagation();
      if (buttons[mentionSel]) insertMention(buttons[mentionSel].dataset.nick);
    } else if (e.key === 'Escape') {
      e.stopImmediatePropagation();
      hideMentions();
    }
  });
  $('chatInput').addEventListener('keydown', (e) => { if (e.key === 'Escape' && replyTo) clearReply(); });

  $('messages').addEventListener('click', (e) => {
    if (e.target.closest('[data-who]')) return;      // клік по ніку — картка людини (web/people.js), а не кнопки повідомлення
    const el = e.target.closest('.msg[data-id]');
    if (!el) return;
    const btn = e.target.closest('[data-a], .mlikes');
    if (btn) {
      if (btn.classList.contains('mlikes') || btn.dataset.a === 'like') likeMessage(+el.dataset.id);
      else if (btn.dataset.a === 'reply') setReply(el);
      else if ((btn.dataset.a === 'pin' || btn.dataset.a === 'del') && window.HModer) HModer.act(btn.dataset.a, el);
      el.classList.remove('act');
      return;
    }
    const quote = e.target.closest('.rq');
    if (quote) {
      const orig = $('messages').querySelector(`.msg[data-id="${quote.dataset.to}"]`);
      if (orig) {
        orig.scrollIntoView({ block: 'center', behavior: 'smooth' });
        orig.classList.remove('flash');
        void orig.offsetWidth;
        orig.classList.add('flash');
      } else toast('Це повідомлення вже випало з історії');
      return;
    }
    if (e.target.closest('a, button')) return;
    // на телефоні наведення нема: тап по повідомленню показує кнопки, тап по іншому — ховає
    $('messages').querySelectorAll('.msg.act').forEach((x) => { if (x !== el) x.classList.remove('act'); });
    el.classList.toggle('act');
  });
  $('messages').addEventListener('dblclick', (e) => {
    const el = e.target.closest('.msg[data-id]');
    if (!el || e.target.closest('a, button, .rq, [data-who]')) return;
    window.getSelection()?.removeAllRanges();
    likeMessage(+el.dataset.id);
  });
  $('chatForm').onsubmit = (e) => {
    e.preventDefault();
    const text = dotToSlash($('chatInput').value.trim());
    if (!text) return;
    if (!conn) { askNick(true); return; }   // лише дивишся — спершу назвись
    if (chatTab !== 'chat') setChatTab('chat');
    const call = replyTo ? conn.invoke('SendReply', text, replyTo.id) : conn.invoke('SendChat', text);
    call
      .then((err) => { if (err) { toast(err, 'err'); return; } $('chatInput').value = ''; hideCmdHint(); clearReply(); })
      .catch((err) => toast('Халепа: не відправилось — ' + err.message, 'err'));
  };
  // Кинути файл: 📎, Ctrl+V з буфера чи перетягнути на балачки. По одному, з прогресом; перший бере підпис із поля й відповідь.
  const upQueue = [];
  let upXhr = null;
  function throwFiles(list) {
    const files = [...(list || [])];
    if (!files.length) return;
    if (!conn || !me.nick) { askNick(true); return; }
    if (!me.account) { toast('Файли кидають лише ті, хто з акаунтом — зареєструй нік', 'err'); askNick(true); return; }
    const blocked = window.HModer && HModer.mediaBlock();   // 🔇 / 🚫 від адміна (web/moder.js)
    if (blocked) { toast(blocked, 'err'); return; }
    for (const f of files) {
      if (f.size > FILE_MAX) toast(`«${f.name}» завеликий — до 32 МБ`, 'err');
      else if (!f.size) toast(`«${f.name}» порожній`, 'err');
      else upQueue.push(f);
    }
    if (chatTab !== 'chat') setChatTab('chat');
    if (!upXhr) nextUpload();
  }
  function nextUpload() {
    const f = upQueue.shift();
    if (!f) { upXhr = null; $('fileUp').hidden = true; return; }
    const caption = $('chatInput').value.trim();
    const reply = replyTo ? replyTo.id : 0;
    const bar = $('fileUp');
    const paint = (part) => {
      bar.querySelector('.fu-n').textContent = `📎 ${f.name || 'файл'} · ${fileSize(f.size)}` + (upQueue.length ? ` (ще ${upQueue.length})` : '');
      bar.querySelector('progress').value = Math.round(part * 100);
    };
    bar.hidden = false;
    paint(0);
    const x = upXhr = new XMLHttpRequest();
    x.open('POST', '/api/chat/file');
    x.setRequestHeader('Content-Type', 'application/octet-stream');
    x.setRequestHeader('X-Nick', encodeURIComponent(me.nick));
    x.setRequestHeader('X-File-Name', encodeURIComponent(f.name || ''));
    if (caption) x.setRequestHeader('X-Caption', encodeURIComponent(caption));
    if (reply) x.setRequestHeader('X-Reply-To', String(reply));
    x.upload.onprogress = (e) => { if (e.lengthComputable) paint(e.loaded / e.total); };
    x.onload = () => {
      let d = null;
      try { d = JSON.parse(x.responseText); } catch { /* без тіла */ }
      if (x.status >= 200 && x.status < 300) {
        // підпис і відповідь пішли з цим файлом — поле чисте (якщо, поки вантажилось, не написали нового)
        if (caption && $('chatInput').value.trim() === caption) $('chatInput').value = '';
        if (reply && replyTo && replyTo.id === reply) clearReply();
      } else toast((d && d.message) || `«${f.name}» не пішов (HTTP ${x.status})`, 'err');
      nextUpload();
    };
    x.onerror = () => { toast(`«${f.name}» не пішов — зв'язок обірвався`, 'err'); nextUpload(); };
    x.onabort = () => { upQueue.length = 0; nextUpload(); };
    x.send(f);
  }
  $('fileBtn').onclick = () => {
    if (!me.account) { throwFiles([{ size: 1 }]); return; }   // гість — та сама підказка «зареєструй нік»
    const blocked = window.HModer && HModer.mediaBlock();
    if (blocked) { toast(blocked, 'err'); return; }
    $('fileInput').click();
  };
  $('fileInput').onchange = () => { throwFiles($('fileInput').files); $('fileInput').value = ''; };
  $('fileUpX').onclick = () => { if (upXhr) upXhr.abort(); };
  $('chatInput').addEventListener('paste', (e) => {
    const files = e.clipboardData && e.clipboardData.files;
    if (!files || !files.length) return;
    e.preventDefault();
    throwFiles(files);
  });
  {
    const zone = $('colChat');
    const hasFiles = (e) => !!e.dataTransfer && [...e.dataTransfer.types].includes('Files');
    let depth = 0;
    zone.addEventListener('dragenter', (e) => { if (!hasFiles(e)) return; e.preventDefault(); depth++; zone.classList.add('drop'); });
    zone.addEventListener('dragover', (e) => { if (hasFiles(e)) e.preventDefault(); });
    zone.addEventListener('dragleave', () => { if (depth && --depth === 0) zone.classList.remove('drop'); });
    zone.addEventListener('drop', (e) => {
      if (!hasFiles(e)) return;
      e.preventDefault();
      depth = 0;
      zone.classList.remove('drop');
      throwFiles(e.dataTransfer.files);
    });
  }
  // Картинка на весь екран — по кліку; ще клік чи Esc — назад. Стерту (понад 10 ГБ) показуємо написом, а не розбитою іконкою.
  const lightbox = document.createElement('div');
  lightbox.className = 'lightbox';
  lightbox.hidden = true;
  lightbox.innerHTML = '<img alt=""><a target="_blank" rel="noopener">Відкрити окремо</a>';
  document.body.appendChild(lightbox);
  lightbox.onclick = (e) => { if (!e.target.closest('a')) lightbox.hidden = true; };
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !lightbox.hidden) { lightbox.hidden = true; e.stopPropagation(); } }, true);
  $('messages').addEventListener('click', (e) => {
    const a = e.target.closest('.mfile.img');
    if (!a || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
    e.preventDefault();
    lightbox.querySelector('img').src = a.href;
    lightbox.querySelector('a').href = a.href;
    lightbox.hidden = false;
  });
  $('messages').addEventListener('error', (e) => {
    const media = e.target.closest && e.target.closest('.mfile');
    if (!media || media.classList.contains('gone')) return;
    const gone = document.createElement('span');
    gone.className = 'mfile gone';
    gone.textContent = '🗑 файл уже прибрано';
    const dl = media.nextElementSibling;
    if (dl && dl.classList.contains('mfile-dl')) dl.remove();
    media.replaceWith(gone);
  }, true);
  // Картинка без розмірів (AVIF, JPEG із кадром далеко) розсуває рядок, коли довантажиться: хто був унизу — лишається внизу.
  $('messages').addEventListener('load', (e) => {
    if (e.target.tagName !== 'IMG' || e.target.getAttribute('width')) return;
    const box = $('messages');
    if (box.scrollHeight - box.scrollTop - box.clientHeight < e.target.offsetHeight + 120) box.scrollTop = box.scrollHeight;
  }, true);

  function setChatTab(tab) {
    // «Стіл» є лише тоді, коли балачка столу живе в панелі; інакше вона — шторка, і вкладки нема.
    if (tab === 'table' && table.mode !== 'rail') tab = 'chat';
    chatTab = tab;
    if (window.HModer) HModer.tab(tab);   // 📌 плашка — лише над Балачками
    $('chatTabs').querySelectorAll('button[data-tab]').forEach((b) => b.classList.toggle('on', b.dataset.tab === tab));
    $('messages').hidden = tab !== 'chat';
    $('log').hidden = tab !== 'log';
    $('tablePane').hidden = tab !== 'table';
    $('logFilters').hidden = tab !== 'log';
    // У столу свій рядок вводу, а «хто на сайті» там лише займав би місце розмови. У Журналі писати нікуди:
    // рядок вводу там лише вводив в оману (написав у Журналі — полетіло в Балачки).
    const general = tab !== 'table';
    $('chatForm').hidden = tab !== 'chat';
    $('online').hidden = !general;
    if (tab !== 'chat') { hideCmdHint(); hideEmoji(); hideMentions(); clearReply(); }
    paintTyping();
    if (tab === 'table') {
      table.unread = 0;
      paintTableBadge();
      scrollTable();
    } else {
      const box = tab === 'chat' ? $('messages') : $('log');
      box.scrollTop = box.scrollHeight;
      fillUp(box);   // під фільтром Журналу скринька може бути майже порожня — тоді старіше просимо самі
    }
    if (chatVisible()) setUnread(0);
  }
  // Лише вкладки: у тому ж рядку живуть 🔔 і ✕, у них свої обробники — інакше клік по дзвіночку «відкривав» порожню вкладку.
  $('chatTabs').querySelectorAll('button[data-tab]').forEach((b) => b.onclick = () => setChatTab(b.dataset.tab));

  // ---------- маршрути ----------
  // Кожен екран має адресу: #efir, #lib/<вкладка>, #games(/…), #stats/<вкладка>, #who/<нік>, #chat (вкладка
  // балачок на телефоні). Хеш — єдине джерело істини: кнопки лише ставлять його, малює applyRoute(), F5 повертає на місце.
  const ROUTES = ['efir', 'lib', 'games', 'stats', 'who', 'lavka', 'chat'];
  const LIB_TABS = ['history', 'likes', 'playlists', 'bans', 'ads', 'feedback', 'photos', 'mod'];
  const ROUTE_TITLE = { efir: 'Ефір', lib: 'Бібліотека', games: 'Ігри', stats: 'Хто скільки', who: 'Профіль', lavka: 'Лавка', chat: 'Балачки' };
  const LIB_TITLE = { history: 'Що вже було', likes: 'Улюблене', playlists: 'Плейлисти', bans: 'Бан-лист', ads: 'Реклама', feedback: 'Пропозиції й баги', photos: 'Фото людей', mod: 'Модерація Балачок' };
  // Вкладки зі списком рядків уміють шукати по собі; у плейлистах шукати нічого.
  const LIB_FIND = { history: 'знайти в історії', likes: 'знайти в улюбленому', bans: 'знайти в бан-листі', ads: 'знайти рекламу', feedback: 'знайти в записках', photos: 'знайти за ніком' };
  // Старі адреси (закладки, посилання в балачках) ведуть туди, куди переїхали їхні сторінки.
  const MOVED = {
    'lib/rating': '#stats/music', 'lib/top': '#stats/music',
    'games/leaders': '#stats/games', 'games/time': '#stats/time', 'games/daily': '#games',
  };
  let libShown = null;    // яку вкладку бібліотеки вже намалювали: щоб не смикати API на кожен маршрут

  function parseHash() {
    const raw = String(location.hash || '').replace(/^#/, '');
    const i = raw.indexOf('/');
    return i < 0 ? { head: raw, tail: '' } : { head: raw.slice(0, i), tail: raw.slice(i + 1) };
  }
  const hashFor = (r) => (r === 'lib' ? '#lib/' + libTab : r === 'stats' ? HPeople.statsHash()
    : r === 'who' ? '#who/' + encodeURIComponent(me.nick || '') : '#' + r);
  function go(hash) {
    if (location.hash === hash) applyRoute(); else location.hash = hash;
  }

  let lastHash = null;
  function applyRoute() {
    let { head, tail } = parseHash();
    // Падельня — шар поверх сайту, а не розділ: під нею лишається той, з якого прийшли (разом із прокруткою).
    // Відкрили одразу #padel (F5, закладка) — під нею лобі ігор.
    padelView(head === 'padel', tail);
    if (head === 'padel') {
      if (lastHash !== null) return;
      head = 'games'; tail = '';
    }
    const moved = MOVED[head + '/' + tail] || (head === 'games' && tail === 'profile' ? '#who/' + encodeURIComponent(me.nick || '') : null);
    if (moved) {
      history.replaceState(null, '', moved);
      ({ head, tail } = parseHash());
    }
    // Перейшов кудись — починаємо згори: інакше з довгого лобі потрапляєш на стіл уже прогорнутим.
    if (lastHash !== null && lastHash !== location.hash) window.scrollTo(0, 0);
    lastHash = location.hash;
    let r = ROUTES.includes(head) ? head : 'efir';
    // На широкому екрані балачки — панель збоку, а не розділ: #chat лише розгортає її.
    if (r === 'chat' && !isMobile()) {
      setChatOpen(true);
      history.replaceState(null, '', hashFor('efir'));
      r = 'efir';
    }
    if (r === 'lib') {
      const want = decodeURIComponent(tail);
      const next = LIB_TABS.includes(want) ? want : 'history';
      if (next !== libTab) { libTab = next; $('libFind').value = ''; }
      $('libTitle').textContent = LIB_TITLE[libTab];
      $('libFind').hidden = !LIB_FIND[libTab];
      $('libFind').placeholder = LIB_FIND[libTab] || '';
    }
    // #who без ніка — це «я»; хто ще не назвався, тому спершу картка «Хто прийшов?».
    if (r === 'who' && !tail) {
      if (!me.nick) { askNick(true); history.replaceState(null, '', hashFor('efir')); r = 'efir'; }
      else { history.replaceState(null, '', hashFor('who')); tail = encodeURIComponent(me.nick); }
    }
    route = r;
    const whoNick = r === 'who' ? decodeURIComponent(tail) : '';
    $('hdrTitle').textContent = r === 'who' && !sameNick(whoNick, me.nick) ? whoNick : r === 'who' ? 'Я' : ROUTE_TITLE[r];
    for (const name of ROUTES) document.body.classList.toggle('route-' + name, r === name);
    document.querySelectorAll('#mainNav button, .mtabs button').forEach((b) => b.classList.toggle('on',
      b.dataset.route === r || (r === 'chat' && b.dataset.route === 'chat')));
    $('meBtn').classList.toggle('on', r === 'who' && sameNick(whoNick, me.nick));
    $('libTabs').querySelectorAll('button').forEach((b) => b.classList.toggle('on', b.dataset.tab === libTab));
    if (r === 'games') HGames.show(head === 'games' ? tail : ''); else HGames.hide();
    if (r === 'stats' || r === 'who') HPeople.show(r, tail); else HPeople.hide();
    if (r === 'lavka') HLavka.show(tail); else HLavka.hide();
    if (r === 'lib' && libShown !== libTab) { libShown = libTab; loadLib(); }
    if (r === 'chat') { const box = $('messages'); box.scrollTop = box.scrollHeight; }
    if (chatVisible()) setUnread(0);
  }
  window.addEventListener('hashchange', applyRoute);
  document.querySelectorAll('#mainNav button, .mtabs button').forEach((b) => b.onclick = () => go(hashFor(b.dataset.route)));

  // ---------- Падельня поверх сайту ----------
  // /padel/ — окрема сторінка, і перехід на неї вивантажував головну разом із плеєром: радіо замовкало. Тепер #padel
  // відкриває її в рамці поверх усього, а головна (ефір, балачки, хаб) живе під нею. Адреса Падельні (#board,
  // #tour/t3…) їде в нашу як #padel/tour/t3 — F5 повертає туди ж; «←» і посилання на головну закривають рамку.
  let padelFrame = null, padelBack = '#games';
  function padelView(on, tail) {
    document.body.classList.toggle('padel-open', on);
    if (!on) {
      if (padelFrame) { padelFrame.remove(); padelFrame = null; }
      return;
    }
    const hash = tail ? '#' + tail : '';
    if (padelFrame) {
      // Адресу змінили руками поверх відкритої рамки — ведемо Падельню туди ж
      try { const w = padelFrame.contentWindow; if (hash && w.location.hash !== hash) w.location.hash = hash; } catch { /* ще вантажиться */ }
      return;
    }
    padelBack = lastHash && !lastHash.startsWith('#padel') ? lastHash : '#games';
    const f = padelFrame = document.createElement('iframe');
    f.className = 'padel-frame';
    f.title = 'Падельня';
    f.allow = 'fullscreen; screen-wake-lock; autoplay; bluetooth';   // bluetooth — брелоки-пульти табло
    f.src = '/padel/' + hash;
    f.addEventListener('load', () => {
      let path = '';
      try { path = f.contentWindow.location.pathname; } catch { /* */ }
      // Рамка таки пішла з Падельні (посилання, яке padel.js не перехопив) — не тримаємо в ній другу головну
      if (path && !path.startsWith('/padel/')) { if (padelFrame === f) go(padelBack); return; }
      try { f.contentWindow.focus(); } catch { /* */ }
    });
    document.body.appendChild(f);
  }
  window.addEventListener('message', (e) => {
    const d = e.data;
    if (!padelFrame || e.source !== padelFrame.contentWindow || e.origin !== location.origin || !d || typeof d.padel !== 'string') return;
    if (d.padel === 'at') {
      // Вкладки Падельні — без нового кроку в історії: їхні кроки вже лежать у рамці, «Назад» пройде їх, потім закриє
      const h = '#padel' + (d.hash ? '/' + d.hash : '');
      if (location.hash !== h) history.replaceState(null, '', h);
    } else if (d.padel === 'leave') {
      go(d.hash && d.hash !== '#' && !d.hash.startsWith('#padel') ? d.hash : padelBack);
    }
  });
  document.addEventListener('visibilitychange', () => { if (chatVisible()) setUnread(0); });

  // ---------- балачки: згорнути / розгорнути ----------
  function setChatOpen(on) {
    chatOpen = on;
    try { localStorage.setItem('chatOpen', on ? '1' : '0'); } catch { /* приватне вікно */ }
    document.body.classList.toggle('chat-collapsed', !on);
    // Згорнув панель біля столу — балачка столу переїжджає в шторку, розгорнув — назад у вкладку.
    placeTable();
    if (on && chatTab !== 'table') { const box = chatTab === 'chat' ? $('messages') : $('log'); box.scrollTop = box.scrollHeight; }
    if (chatVisible()) setUnread(0);
  }
  const toggleChat = () => (isMobile() ? go(route === 'chat' ? hashFor('efir') : '#chat') : setChatOpen(!chatOpen));
  $('chatToggle').onclick = toggleChat;
  $('chatClose').onclick = () => setChatOpen(false);
  setChatOpen(chatOpen);

  // ---------- search / add ----------
  const q = $('q'), results = $('results');
  let lastResults = [];
  let sel = -1;
  function showResults(list, hint, wait) {
    sel = -1;
    if (hint) { results.innerHTML = `<div class="hint">${wait ? '<span class="spin"></span>' : ''}${esc(hint)}</div>`; return; }
    results.innerHTML = list.map((r, i) => `<div class="result" data-i="${i}">
        ${r.thumbUrl ? `<img src="${esc(r.thumbUrl)}" alt="">` : '<div></div>'}
        <div style="min-width:0"><div class="t">${esc(r.title)}</div><div class="a">${esc(r.artist)}${r.album ? ' · ' + esc(r.album) : ''}</div></div>
        <div class="d">${fmt(r.durationSec)}</div>
      </div>`).join('');
    results.querySelectorAll('.result').forEach((el) => el.onclick = () => addPick(list[+el.dataset.i]));
  }
  async function search(text) {
    if (text === lastQuery) return;
    lastQuery = text;
    if (!text) { results.innerHTML = ''; lastResults = []; return; }
    const links = splitLinks(text);
    if (links.length) {
      // посилання не шукаємо, лише кажемо, що буде на Enter
      lastResults = [];
      showResults([], links.length > 1 ? `Enter — закину всі ${plural(links.length, 'посилання', 'посилання', 'посилань')} по черзі`
        : maybeAlbum(links[0]) ? 'Enter — покажу трекліст: закинеш усе або вибрані' : 'Enter — закину за посиланням');
      return;
    }
    showResults([], 'шукаю…', true);
    try {
      const list = await api('GET', `/api/search?q=${encodeURIComponent(text)}`);
      if (lastQuery !== text) return;
      lastResults = list;
      showResults(list, list.length ? null : 'нічого не знайшов, спробуй інакше або кинь посилання');
    } catch (e) { showResults([], 'ой-йой, пошук упав: ' + e.message); }
  }
  q.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => search(q.value.trim()), 350); });
  q.addEventListener('keydown', (e) => {
    const items = results.querySelectorAll('.result');
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (!items.length) return;
      e.preventDefault();
      sel = (sel + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
      items.forEach((el, i) => el.classList.toggle('sel', i === sel));
      items[sel].scrollIntoView({ block: 'nearest' });
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (sel >= 0 && lastResults[sel]) addPick(lastResults[sel]); else addFromInput();
    } else if (e.key === 'Escape') { results.innerHTML = ''; q.blur(); }
  });
  document.addEventListener('click', (e) => { if (!e.target.closest('.add')) results.innerHTML = ''; });
  q.addEventListener('focus', () => { if (lastResults.length && q.value.trim() === lastQuery) showResults(lastResults); });
  $('addBtn').onclick = addFromInput;
  // Каркас ігор слухає document раніше за нас (core.js підключений вище за app.js), тож клавішу,
  // яку вже з'їла гра, він позначає preventDefault — і ми в неї не лізимо.
  document.addEventListener('keydown', (e) => {
    if (e.defaultPrevented || e.metaKey || e.ctrlKey || e.altKey) return;
    const t = e.target;
    if (t && ((t.matches && t.matches('input, textarea, select')) || t.isContentEditable)) return;
    // Українська розкладка: на місці «/» там «.», а на місці «]» — «ї». Хай працюють обидві.
    if (e.key === '/' || e.key === '.') { e.preventDefault(); go(hashFor('efir')); q.focus(); return; }
    if (e.key === ']' || e.key === 'ї' || e.key === 'Ї') { e.preventDefault(); toggleChat(); return; }
    if (e.key === '?') { e.preventDefault(); showKeys(); return; }
    if (e.key === '1') { e.preventDefault(); go(hashFor('efir')); }
    else if (e.key === '2') { e.preventDefault(); go(hashFor('lib')); }
    else if (e.key === '3') { e.preventDefault(); go(hashFor('games')); }
    else if (e.key === '4') { e.preventDefault(); go(hashFor('stats')); }
  });
  // ---------- «💡 Розробнику»: пропозиції й баги ----------
  // Записка йде в базу (Feedback.cs) разом із тим, де людина на сайті, розміром екрана й браузером — так баг легше
  // відтворити. Кожна записка — переписка: у «Моїх записках» видно все, що відповів розробник, і там же можна
  // відписати. У скількох записках є непрочитана відповідь — число на 💡; нове прилітає хабом (fbUnread), без F5.
  const FB_KINDS = {
    idea: { ph: 'Що варто додати? Наприклад: «щоб у балачках можна було закріпити повідомлення»', note: 'Разом із текстом піде, де ти на сайті, — щоб розробник зрозумів, про що мова.' },
    change: { ph: 'Що змінити й чому? Наприклад: «на телефоні черга завелика — хай згортається»', note: 'Разом із текстом піде, де ти на сайті, — щоб розробник зрозумів, про що мова.' },
    bug: { ph: 'Що робиш → що стається → що мало б статися. Наприклад: «тисну Скіп — нічого, а мало перемкнути»', note: 'Разом із текстом піде, де ти на сайті, розмір екрана й браузер — так баг легше знайти.' },
  };
  const FB_STATUS = { new: ['нове', ''], seen: ['переглянуто', ''], planned: ['у планах', 'warn'], done: ['зроблено', 'ok'], nope: ['не буде', 'err'] };
  const FB_ICON = { idea: '💡', change: '✏', bug: '🐞' };
  const FB_MAX_MSG = 1000;       // як Feedback.MaxMsg
  let fbKind = 'idea';
  /// Розмір вікна до записки — ще й щільність пікселів і весь екран: «на маку все стало менше» (записка #27) з одного
  /// «1512×862» не розбереш. devicePixelRatio у Chrome множиться на масштаб сторінки (мак 2 → 1.8 при 90%), а екран
  /// каже, ноут це (1512×982) чи зовнішній монітор. Сервер ріже до 40 знаків — вкладаємось.
  const fbScreen = () => {
    const d = Math.round((window.devicePixelRatio || 1) * 100) / 100;
    return `${window.innerWidth}×${window.innerHeight} · ${d}x · екран ${screen.width}×${screen.height}`;
  };
  let fbMineUnread = 0;         // у скількох своїх записках нова відповідь (людині; адміну 💡 рахує інше)
  // Повідомлення розробника, нові на момент показу: сервер уже вважає їх прочитаними, а підсвітка тримається, поки
  // вікно відкрите — інакше перше ж перемальовування (сама відписала) гасило б її посеред читання.
  const fbMineFresh = new Set();
  function setFbKind(k) {
    fbKind = FB_KINDS[k] ? k : 'idea';
    $('fbKinds').querySelectorAll('[data-k]').forEach((b) => b.classList.toggle('on', b.dataset.k === fbKind));
    $('fbText').placeholder = FB_KINDS[fbKind].ph;
    $('fbNote').textContent = FB_KINDS[fbKind].note;
  }
  const fbStatusChip = (s) => { const [l, cls] = FB_STATUS[s] || [s, '']; return `<span class="chip ${cls}">${esc(l)}</span>`; };
  /// Номер записки — той самий id, що в базі: «записка #27» однаково зрозуміла людині, розробнику й пошуку («#27»).
  const fbNo = (id) => `<span class="fbno" title="Номер записки">#${Number(id)}</span>`;
  /// Одне повідомлення переписки. mineDev — чи «свої» тут повідомлення розробника (так бачить адмін); свої — праворуч.
  /// Розробник переставив стан — рядок посередині, а не бульбашка: це подія, а не слова.
  function fbBubble(m, mineDev, fresh) {
    const at = esc(dayTime(m.at));
    const hl = fresh.has(m.id) ? ' fresh' : '';
    if (m.kind === 'status') return `<div class="fbx-st${hl}">Стан записки: ${fbStatusChip(m.text)}<span>${at}</span></div>`;
    const mine = !!m.dev === mineDev;
    const who = !mine && m.dev ? '<div class="fbb-who">Розробник</div>' : '';
    return `<div class="fbb ${mine ? 'me' : 'them'}${hl}">${who}<div class="fbb-text">${linkify(m.text)}</div><div class="fbb-at">${at}</div></div>`;
  }
  /// Поле відповіді під запискою. Не <form>: «Мої записки» живуть усередині форми нової записки, а форма у формі не буває.
  const fbSayBox = (ph) => `<div class="fbsay"><textarea rows="1" maxlength="${FB_MAX_MSG}" placeholder="${esc(ph)}" aria-label="Відповісти"></textarea><button type="button" class="ghost">Тяпнути</button></div>`;
  const fbGrow = (t) => { t.style.height = 'auto'; t.style.height = `${Math.min(t.scrollHeight + 2, 160)}px`; };
  /// Недописане в полях відповіді (і курсор) переживає перемальовування: нове повідомлення прилітає й тоді, коли пишеш.
  function fbDrafts(root) {
    const map = new Map();
    root.querySelectorAll('[data-id] .fbsay textarea').forEach((t) => {
      const focus = document.activeElement === t;
      if (t.value || focus) map.set(t.closest('[data-id]').dataset.id, { v: t.value, focus, at: t.selectionStart });
    });
    return map;
  }
  function fbRestore(root, drafts) {
    for (const [id, d] of drafts) {
      const t = root.querySelector(`[data-id="${id}"] .fbsay textarea`);
      if (!t) continue;
      t.value = d.v;
      if (d.v) fbGrow(t);
      if (d.focus) { t.focus(); try { t.setSelectionRange(d.at, d.at); } catch { /* не текст */ } }
    }
  }
  /// Enter — тяпнути, Shift+Enter — новий рядок; send(id, text, textarea) — куди саме.
  function fbWireSay(root, send) {
    root.querySelectorAll('[data-id] .fbsay').forEach((box) => {
      const id = box.closest('[data-id]').dataset.id;
      const t = box.querySelector('textarea');
      const b = box.querySelector('button');
      const go = () => {
        const text = t.value.trim();
        if (!text) { t.focus(); return; }
        busy(b, '…', () => send(id, text, t));
      };
      b.onclick = go;
      t.addEventListener('input', () => fbGrow(t));
      t.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); go(); } });
    });
  }
  function paintFbBadge(n) {
    if (me.role === 'admin') return;
    fbMineUnread = n;
    $('fbBadge').hidden = !n;
    $('fbBadge').textContent = n > 99 ? '99+' : String(n);
    $('fbBtn').title = n ? `Розробник відповів у записках: ${n}` : 'Пропозиції й баги — розробнику';
  }
  // Свої записки справді видно: вікно відкрите, «Мої записки» розгорнуті, вкладка браузера спереду.
  const fbMineVisible = () => !$('fbModal').hidden && $('fbMine').open && !document.hidden;
  async function readMine() {
    try { paintFbBadge(((await api('POST', '/api/feedback/mine/read', {})) || {}).unread || 0); } catch { /* наступного разу */ }
  }
  const fbMineHot = (x) => (x.msgs || []).some((m) => fbMineFresh.has(m.id));
  /// Записка людини. Поле відповіді — там, де розробник уже щось написав (чи де людина вже дописує); на решті —
  /// лише «✏ Доповнити»: тридцять порожніх полів підряд перетворили б «Мої записки» на анкету.
  function fbMineCard(x, typing) {
    const msgs = x.msgs || [];
    const talk = msgs.some((m) => m.dev);
    return `<div class="fbm${fbMineHot(x) ? ' fresh' : ''}" data-id="${x.id}">
        <div class="fbm-head">${FB_ICON[x.kind] || '💬'} ${fbNo(x.id)} ${fbStatusChip(x.status)}<span class="muted small">${esc(dayTime(x.at))}</span></div>
        <div class="fbt"><div class="fbb me"><div class="fbb-text">${esc(x.text)}</div></div>${msgs.map((m) => fbBubble(m, false, fbMineFresh)).join('')}</div>
        ${talk || typing ? fbSayBox(talk ? 'Відповісти… (Enter)' : 'Доповнити… (Enter)') : '<button type="button" class="ghost fbadd">✏ Доповнити</button>'}
      </div>`;
  }
  async function loadMyFeedback(open) {
    if (!me.nick) return;
    let r;
    try { r = await api('GET', '/api/feedback/mine'); } catch { return; }
    const items = (r && r.items) || [];
    paintFbBadge((r && r.unread) || 0);
    for (const x of items) for (const m of x.msgs || []) if (m.fresh) fbMineFresh.add(m.id);
    const unreadNow = items.some((x) => x.unread);
    const hot = items.filter(fbMineHot).length;   // нове на момент показу: підпис тримається, поки вікно відкрите
    $('fbMine').hidden = !items.length;
    $('fbMineN').textContent = items.length ? `· ${items.length}` + (hot ? ` · нових відповідей ${hot}` : '') : '';
    const list = $('fbMineList');
    const drafts = fbDrafts(list);
    // записки з новою відповіддю — згори (і там лишаються, поки вікно відкрите), далі — як дав сервер: свіжа розмова вище
    const order = items.map((x, i) => [x, i]).sort((a, b) => (Number(fbMineHot(b[0])) - Number(fbMineHot(a[0]))) || a[1] - b[1]);
    list.innerHTML = order.map(([x]) => fbMineCard(x, drafts.has(String(x.id)))).join('');
    fbRestore(list, drafts);
    fbWireSay(list, sayMine);
    list.querySelectorAll('.fbadd').forEach((b) => b.onclick = () => {
      const card = b.closest('[data-id]');
      b.outerHTML = fbSayBox('Доповнити… (Enter)');
      fbWireSay(card, sayMine);
      card.querySelector('.fbsay textarea').focus();
    });
    if (open) $('fbMine').open = true;
    if (unreadNow && fbMineVisible()) {
      readMine();
      if (open) list.querySelector('.fbm.fresh')?.scrollIntoView({ block: 'nearest' });
    }
  }
  async function sayMine(id, text, t) {
    try {
      await api('POST', `/api/feedback/${id}/msg`, { text });
      t.value = '';
      await loadMyFeedback(false);
    } catch (err) { fail(err); }
  }
  $('fbMine').addEventListener('toggle', () => { if (fbMineUnread && fbMineVisible()) readMine(); });
  /// Хаб: розробник відповів (або своє прочитане на іншій вкладці). Вікно відкрите — перемалювати, закрите — число й тост.
  function fbOnUnread(x) {
    if (me.role === 'admin') return;
    const n = (x && x.count) || 0;
    if (!$('fbModal').hidden) { loadMyFeedback(false); return; }
    if (n > fbMineUnread) toast('💡 Розробник відповів на твою записку — глянь у «Моїх записках»', 'ok');
    paintFbBadge(n);
  }
  function openFeedback() {
    setFbKind(fbKind);
    $('fbCount').textContent = `${$('fbText').value.length} / 2000`;
    $('fbModal').hidden = false;
    // Є нова відповідь — одразу до неї, а не в поле нової записки (на телефоні клавіатура заступила б відповідь).
    // На сенсорному екрані поле само не фокусується зовсім: клавіатура вискакувала б і закривала пів вікна.
    if ((!fbMineUnread || me.role === 'admin') && matchMedia('(pointer: fine)').matches) setTimeout(() => $('fbText').focus(), 50);
    loadMyFeedback(fbMineUnread > 0 && me.role !== 'admin');
  }
  function closeFeedback() {
    $('fbModal').hidden = true;
    fbMineFresh.clear();
  }
  $('fbBtn').onclick = openFeedback;
  $('fbClose').onclick = closeFeedback;
  $('fbX').onclick = closeFeedback;
  $('fbModal').addEventListener('click', (e) => { if (e.target === $('fbModal')) closeFeedback(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !$('fbModal').hidden) closeFeedback(); });
  $('fbKinds').querySelectorAll('[data-k]').forEach((b) => b.onclick = () => { setFbKind(b.dataset.k); $('fbText').focus(); });
  $('fbText').addEventListener('input', () => { $('fbCount').textContent = `${$('fbText').value.length} / 2000`; });
  $('fbForm').onsubmit = (e) => {
    e.preventDefault();
    const text = $('fbText').value.trim();
    if (text.length < 5) { toast('Тяпни трохи більше — хоч кілька слів', 'err'); $('fbText').focus(); return; }
    busy($('fbSend'), 'надсилаю…', async () => {
      try {
        const r = await api('POST', '/api/feedback', {
          kind: fbKind, text, place: location.hash || '#efir', screen: fbScreen(),
          ua: navigator.userAgent.slice(0, 300),
        });
        ok(r);
        $('fbText').value = '';
        $('fbCount').textContent = '0 / 2000';
        loadMyFeedback(true);
        if (me.role === 'admin') loadFeedbackCount();
      } catch (err) { fail(err); }
    });
  };
  /// Адміну — скільки записок чекає: нові й ті, де людина відписала. Бейдж на 💡 і на вкладці «Пропозиції».
  function paintDevCount(w) {
    if (me.role !== 'admin' || !w) return;
    const n = w.count || 0;
    for (const id of ['fbBadge', 'fbTabBadge']) { $(id).hidden = !n; $(id).textContent = n; }
    $('fbBtn').title = n ? `Чекає записок: ${n}` + (w.replies ? ` (відповіли: ${w.replies})` : '') : 'Пропозиції й баги — розробнику';
  }
  async function loadFeedbackCount() {
    if (me.role !== 'admin') return;
    try { paintDevCount(await api('GET', '/api/feedback/new-count')); } catch { /* наступного разу */ }
  }
  /// Хаб: нова записка чи відповідь людини. Відкрита вкладка «Пропозиції» перемальовується (недописане в полях
  /// переживе) — з маленькою паузою, бо сервер шле це й на власні дії розробника, після яких список і так тягнеться.
  let fbRedraw = 0;
  function fbOnDev(w) {
    if (me.role !== 'admin') return;
    paintDevCount(w);
    if (route === 'lib' && libTab === 'feedback') { clearTimeout(fbRedraw); fbRedraw = setTimeout(loadLib, 400); }
    else if (libShown === 'feedback') libShown = null;   // вкладку малювали раніше — зайде, хай потягне свіже
  }
  /// Після реконекту (здебільшого — деплой): поки зв'язку не було, розробник міг відповісти.
  function fbResync() {
    if (me.role === 'admin') { loadFeedbackCount(); fbOnDev(null); }   // null — число вже тягнеться, лише перемалювати
    else if (me.nick) loadMyFeedback(false);
  }
  // Вкладку браузера повернули наперед — те, що вже на екрані, тепер справді прочитане.
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) return;
    if (me.role !== 'admin' && fbMineUnread && fbMineVisible()) readMine();
    if (me.role === 'admin' && fbWaitRead.length && route === 'lib' && libTab === 'feedback') readAsDev();
  });

  // ---------- шпаргалка клавіш ----------
  function showKeys() { $('keysModal').hidden = false; $('keysClose').focus(); }
  $('keysClose').onclick = () => { $('keysModal').hidden = true; };
  $('keysModal').addEventListener('click', (e) => { if (e.target === $('keysModal')) $('keysModal').hidden = true; });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !$('keysModal').hidden) $('keysModal').hidden = true; });

  // Поле звільняється одразу, як тільки закинув: наступне посилання можна вставляти, поки сайт розбирає попереднє.
  // (Раніше поле чистила відповідь на старий запит — і стирала вже вставлене нове посилання.)
  function takeInput() {
    const text = q.value.trim();
    q.value = '';
    lastQuery = '';
    lastResults = [];
    results.innerHTML = '';
    return text;
  }
  function addPick(r) {
    takeInput();
    queueAdd({ pick: r }, `${r.artist} — ${r.title}`, 'input');
  }
  function addFromInput() {
    const text = q.value.trim();
    if (!text) { q.focus(); return; }
    const links = splitLinks(text);
    if (!links.length && lastResults.length && lastQuery === text) return addPick(lastResults[0]);
    takeInput();
    if (links.length) submitLinks(links, 'input');
    else queueAdd({ input: text }, `«${text}»`, 'input');
    if (!isMobile()) q.focus();   // на телефоні фокус знову відкрив би клавіатуру
  }

  // ---------- закидання по черзі ----------
  // Кожне посилання чи пошук — окреме завдання, і на сервер вони йдуть по одному, у тому порядку, як їх кидали:
  // так і в черзі вони стануть так само, а друге, кинуте поки сайт розбирає перше, не губиться і не ламає першого.
  // Під пошуком видно, що саме зараз розбирається, що чекає і чим скінчилось.
  const LINK_RX = /(?:https?:\/\/|spotify:(?:track|album|playlist):)\S+?(?=https?:\/\/|spotify:(?:track|album|playlist):|\s|$)/gi;
  /// Посилання з тексту: через пробіл, з нового рядка чи зліплені докупи (друге вставили за першим) — кожне окремо.
  const splitLinks = (text) => (String(text || '').match(LINK_RX) || []).map((s) => s.replace(/[.,;!?)\]»"']+$/, '')).filter(Boolean);
  /// Альбом чи плейлист: Spotify, альбом YouTube Music (browse/MPREb_…) і ?list= без v= — крім міксів RD… (див. Albums.Detect).
  function isAlbumLink(s) {
    if (/^spotify:(album|playlist):/i.test(s) || /open\.spotify\.com\/(?:intl-[a-z-]+\/)?(?:embed\/)?(album|playlist)\//i.test(s)) return true;
    let u;
    try { u = new URL(s); } catch { return false; }
    if (!/(^|\.)youtube\.com$/i.test(u.hostname)) return false;
    if (/^\/browse\/MPREb_/.test(u.pathname)) return true;
    const list = u.searchParams.get('list');
    return !!list && !u.searchParams.has('v') && !/^RD/.test(list) && !['LL', 'WL', 'LM'].includes(list);
  }
  /// Коротке spotify.link з телефона може вести і на трек, і на альбом — це скаже тільки сервер.
  const maybeAlbum = (s) => isAlbumLink(s) || /^https?:\/\/spotify\.link\//i.test(s);
  /// open.spotify.com/track/6UWIAE… — щоб рядок не розтягувався на пів екрана.
  function shortUrl(s) {
    let t = s;
    try {
      const u = new URL(s);
      const id = u.searchParams.get('v') ? '?v=' + u.searchParams.get('v') : u.searchParams.get('list') ? '?list=' + u.searchParams.get('list') : '';
      t = (u.hostname.replace(/^www\./, '') + u.pathname).replace(/\/$/, '') + id;
    } catch { /* spotify:track:… */ }
    return t.length > 52 ? t.slice(0, 51) + '…' : t;
  }

  const adds = [];                 // { id, kind: add|album, body, label, from: input|drop|album, state: wait|run|ok|err|handoff, msg }
  let addSeq = 0, addBusy = false;

  /// run — кидок, за яким стежить оверлей (див. dropText): завдання прив'язується до нього ще до першого малювання.
  function submitLinks(links, from, run) {
    return links.map((link) => (maybeAlbum(link) ? openAlbum(link, from, run) : queueAdd({ input: link }, shortUrl(link), from, run)));
  }
  function newJob(kind, label, from, run, extra) {
    const job = { id: ++addSeq, kind, label, from, state: kind === 'album' ? 'run' : 'wait', msg: '', run, ...extra };
    if (run) run.jobs.push(job);
    adds.push(job);
    drawAdds();
    return job;
  }
  function queueAdd(body, label, from, run) {
    const job = newJob('add', label, from, run, { body });
    pumpAdds();
    return job;
  }
  async function pumpAdds() {
    if (addBusy) return;
    addBusy = true;
    try {
      for (let job; (job = adds.find((j) => j.kind === 'add' && j.state === 'wait'));) {
        job.state = 'run';
        drawAdds();
        try {
          const res = await api('POST', '/api/queue', job.body);
          job.state = 'ok';
          job.msg = res.message || 'Закинуто';
        } catch (e) {
          job.state = 'err';
          job.msg = e.message;
        }
        settle(job);
      }
    } finally { addBusy = false; }
  }
  /// Скінчене ще трохи видно (помилку — довше, щоб встигнути прочитати), далі рядок зникає сам.
  function settle(job) {
    drawAdds();
    setTimeout(() => dropJob(job), job.state === 'err' ? 10000 : 4000);
  }
  function dropJob(job) {
    const i = adds.indexOf(job);
    if (i >= 0) { adds.splice(i, 1); drawAdds(); }
  }
  function drawAdds() {
    const box = $('adding');
    const shown = adds.filter((j) => j.state !== 'handoff');
    box.hidden = !shown.length;
    box.innerHTML = shown.map((j) => {
      const icon = j.state === 'run' ? '<span class="spin"></span>' : `<span class="aj-ico">${{ wait: '⏳', ok: '✓', err: '✕' }[j.state]}</span>`;
      const text = j.state === 'ok' ? j.msg : j.state === 'err' ? `${j.label}: ${j.msg}` : j.label;
      const note = j.state === 'wait' ? 'чекає' : j.state === 'run' ? (j.kind === 'album' ? 'тягну трекліст…' : 'розбираю…') : '';
      return `<div class="add-job ${j.state}" data-id="${j.id}">${icon}<span class="aj-text">${esc(text)}</span>`
        + `${note ? `<span class="aj-note">${note}</span>` : ''}${j.state === 'err' ? '<button class="ghost icon aj-x" title="Сховати">✕</button>' : ''}</div>`;
    }).join('');
    box.querySelectorAll('.aj-x').forEach((b) => b.onclick = () => dropJob(adds.find((j) => j.id === +b.closest('.add-job').dataset.id)));
    drawDropProgress();
  }

  // ---------- альбом чи плейлист з посилання ----------
  // Наосліп не закидаємо: спершу трекліст під пошуком — тоді все в чергу по порядку, впереміш, лише вибрані,
  // по одному, або зберегти плейлистом сайту. Сервер шукає Spotify-альбом у YouTube Music цілим, тож гратимуть
  // саме альбомні версії; що не знайшлось — видно одразу, а не посеред вечора.
  let album = null;                // { url, data, off: Set(id) — зняті галочки, sig }

  function openAlbum(url, from, run) {
    const job = newJob('album', shortUrl(url), from, run);
    loadAlbum(job, url);
    return job;
  }
  async function loadAlbum(job, url) {
    try {
      const data = await api('GET', '/api/album?url=' + encodeURIComponent(url));
      job.state = 'ok';
      job.msg = `${data.kind === 'album' ? 'Альбом' : 'Плейлист'} «${data.title}»: ${tracksN(data.tracks.length)} — трекліст під пошуком`;
      showAlbum(url, data);
    } catch (e) {
      if (e.data && e.data.notAlbum) {
        // коротке spotify.link вело на трек — закидаємо як звичайне посилання
        job.state = 'handoff';
        queueAdd({ input: url }, job.label, job.from, job.run);
        dropJob(job);
        return;
      }
      job.state = 'err';
      job.msg = e.message;
    }
    settle(job);
  }
  function showAlbum(url, data) {
    album = { url, data, off: new Set(), sig: '' };
    drawAlbum();
    if (route !== 'efir') go(hashFor('efir'));
    setTimeout(() => $('album').scrollIntoView({ block: 'nearest', behavior: 'smooth' }), 80);
  }
  function closeAlbum() {
    album = null;
    drawAlbum();
  }
  /// Що з альбому вже в черзі чи грає: панель перемальовуємо, лише коли це змінилось, а не на кожен стан.
  function albumSig() {
    if (!album || !state) return '';
    const ids = new Set(album.data.tracks.filter((t) => t.match).map((t) => t.match.id));
    return state.queue.filter((x) => ids.has(x.track.id)).map((x) => x.track.id).join(',') + '|' + (state.now.track && ids.has(state.now.track.id) ? state.now.track.id : '');
  }
  function albumRow(t, a) {
    const m = t.match;
    // виконавця треку показуємо, лише коли він не той, що в альбому: у збірнику чи плейлисті, а не дев'ять разів «John Lennon, Yoko Ono…»
    const lead = String(a.artist || '').split(/,\s|\s&\s|\sі\s/)[0].trim().toLowerCase();
    const by = t.artist && (a.kind !== 'album' || !lead || !t.artist.toLowerCase().includes(lead)) ? ` <span class="muted">· ${esc(t.artist)}</span>` : '';
    if (!m) {
      return `<li class="miss" title="Не знайшлось у YouTube Music — пропущу"><span class="n"><span>${t.n}</span></span>`
        + `<div class="t">${esc(t.title)}${by}</div><span class="d">${fmt(t.durationSec)}</span><span class="chip">нема</span></li>`;
    }
    const off = album.off.has(m.id);
    const onAir = state && state.now.track && state.now.track.id === m.id;
    const queued = state && state.queue.some((x) => x.track.id === m.id);
    return `<li class="${off ? 'off' : ''}" data-id="${esc(m.id)}">
        <label class="n" title="${off ? 'Повернути до вибраних' : 'Прибрати з вибраних'}"><input type="checkbox" ${off ? '' : 'checked'}><span>${t.n}</span></label>
        <div class="t">${esc(t.title)}${by}</div>
        <span class="d">${fmt(m.durationSec || t.durationSec)}</span>
        ${onAir ? '<span class="chip ok">грає</span>' : queued ? '<span class="chip ok">у черзі</span>' : '<button class="ghost q1" title="Закинути лише цей трек">в чергу</button>'}
      </li>`;
  }
  function drawAlbum() {
    const box = $('album');
    if (!album) { box.hidden = true; box.innerHTML = ''; return; }
    const a = album.data;
    const found = a.tracks.filter((t) => t.match);
    const picked = found.filter((t) => !album.off.has(t.match.id));
    const src = a.source === 'spotify' ? 'Spotify' : 'YouTube Music';
    const sub = [a.artist, a.year, tracksN(a.tracks.length), `${Math.max(1, Math.round(a.durationSec / 60))} хв`].filter(Boolean).join(' · ');
    const missing = a.tracks.length - found.length;
    const scroll = box.querySelector('.album-tracks')?.scrollTop || 0;
    album.sig = albumSig();
    box.hidden = false;
    box.innerHTML = `<div class="album-head">
        ${a.thumbUrl ? `<img src="${esc(a.thumbUrl)}" alt="">` : '<div class="noimg">💿</div>'}
        <div class="album-meta">
          <div class="album-kind">${a.kind === 'album' ? 'Альбом' : 'Плейлист'} · ${src}</div>
          <div class="album-title" title="${esc(a.title)}">${esc(a.title)}</div>
          <div class="album-sub">${esc(sub)}</div>
        </div>
        <button class="ghost icon album-x" title="Закрити">✕</button>
      </div>
      <div class="album-btns">
        <button class="primary" data-go="order" ${picked.length ? '' : 'disabled'} title="По порядку, як в ${a.kind === 'album' ? 'альбомі' : 'плейлисті'}">▶ ${picked.length === found.length ? 'Усе' : 'Вибрані'} в чергу · ${picked.length}</button>
        <button data-go="shuffle" ${picked.length > 1 ? '' : 'disabled'}>🔀 Упереміш</button>
        <button class="ghost" data-go="save" ${found.length ? '' : 'disabled'} title="Зберегти плейлистом сайту: він з'явиться в Бібліотеці">＋ У плейлисти</button>
        <a class="chip album-src" href="${esc(a.url)}" target="_blank" rel="noopener" title="Відкрити в ${src}">↗ ${src}</a>
      </div>
      <ol class="album-tracks">${a.tracks.map((t) => albumRow(t, a)).join('')}</ol>
      ${missing ? `<div class="album-foot">${missing === a.tracks.length ? 'Жоден трек' : plural(missing, 'трек', 'треки', 'треків')} не знайшлось у YouTube Music — ${missing === a.tracks.length ? 'закидати нема чого' : 'їх пропущу'}. Спробуй пошукати ${missing === 1 ? 'його' : 'їх'} за назвою.</div>` : ''}
      ${a.kind === 'playlist' && a.tracks.length >= 100 ? '<div class="album-foot">Тут перші 100 треків плейлиста: більше за раз не віддають.</div>' : ''}`;
    box.querySelector('.album-tracks').scrollTop = scroll;
    box.querySelector('.album-x').onclick = closeAlbum;
    box.querySelectorAll('.album-tracks li[data-id]').forEach((li) => {
      const id = li.dataset.id;
      li.querySelector('input').onchange = (e) => { if (e.target.checked) album.off.delete(id); else album.off.add(id); drawAlbum(); };
      li.querySelector('.q1')?.addEventListener('click', () => {
        const t = a.tracks.find((x) => x.match && x.match.id === id);
        queueAdd({ pick: t.match }, `${t.match.artist} — ${t.match.title}`, 'album');
      });
    });
    const url = album.url;
    box.querySelectorAll('[data-go="order"], [data-go="shuffle"]').forEach((b) => b.onclick = (e) => busy(e.currentTarget, 'закидаю…', async () => {
      const ids = picked.length === found.length ? null : picked.map((t) => t.match.id);
      try {
        ok(await api('POST', '/api/album/queue', { url, shuffle: b.dataset.go === 'shuffle', ids }));
        if (album && album.url === url) closeAlbum();   // далі все видно в черзі
      } catch (err) { fail(err); }
    }));
    box.querySelector('[data-go="save"]').onclick = (e) => busy(e.currentTarget, 'зберігаю…', async () => {
      try {
        ok(await api('POST', '/api/album/playlist', { url }));
        if (route === 'lib' && libTab === 'playlists') renderPlaylists();
      } catch (err) { fail(err); }
    });
  }

  // ---------- голосові: записати і поставити в чергу ----------
  // MediaRecorder пише в тому форматі, який уміє браузер (webm/opus, у Safari mp4) — сервер сам
  // перегонить його в mp3 і кладе в кеш, далі запис іде чергою як звичайний трек.
  const recBox = $('rec');
  const voiceMax = () => (state && state.voiceMaxSeconds) || 0;
  const canRecord = () => !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
  const MIMES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];
  let recorder = null, recStream = null, recChunks = [], recStartedAt = 0, recTimer = 0, recTossed = false;
  let recBlob = null, recUrl = null, recActx = null, recAnalyser = null, recRaf = 0;

  async function startRec() {
    if (recorder) return;
    if (!voiceMax()) { toast('Халепа: голосові вимкнені', 'err'); return; }
    if (!canRecord()) { toast('Халепа: цей браузер не вміє писати звук (потрібен https і свіжий Chrome, Firefox або Safari)', 'err'); return; }
    if (!me.nick) { askNick(); return; }
    let stream;
    try { stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } }); }
    catch (e) { toast(e.name === 'NotAllowedError' ? 'Халепа: мікрофон не дозволено — дозволь у браузері й спробуй ще' : 'Халепа: мікрофон не відкрився — ' + e.message, 'err'); return; }
    dropRecBlob();
    recStream = stream;
    recChunks = [];
    recTossed = false;
    const type = MIMES.find((m) => MediaRecorder.isTypeSupported(m));
    try { recorder = new MediaRecorder(stream, type ? { mimeType: type, audioBitsPerSecond: 96000 } : undefined); }
    catch { recorder = new MediaRecorder(stream); }
    recorder.ondataavailable = (e) => { if (e.data && e.data.size) recChunks.push(e.data); };
    recorder.onstop = finishRec;
    recorder.start();
    recStartedAt = Date.now();
    drawRecLive();
    recTimer = setInterval(() => {
      const sec = (Date.now() - recStartedAt) / 1000;
      const el = $('recTime');
      if (el) el.textContent = fmt(sec);
      if (sec >= voiceMax()) stopRec();   // довше сервер усе одно відріже
    }, 200);
    startMeter(stream);
  }

  function stopRec() {
    clearInterval(recTimer);
    recTimer = 0;
    if (recorder && recorder.state !== 'inactive') { try { recorder.stop(); } catch { /* уже стало */ } }
  }
  function cancelRec() { recTossed = true; stopRec(); }

  function finishRec() {
    const type = (recorder && recorder.mimeType) || 'audio/webm';
    const sec = Math.round((Date.now() - recStartedAt) / 1000);
    const blob = new Blob(recChunks, { type });
    recorder = null;
    recChunks = [];
    stopMeter();
    releaseMic();
    if (recTossed) { closeRec(); return; }
    if (blob.size < 1024) { closeRec(); toast('Халепа: нічого не записалось — ану ще раз', 'err'); return; }
    recBlob = blob;
    recUrl = URL.createObjectURL(blob);
    drawRecPreview(sec);
  }

  function drawRecLive() {
    recBox.hidden = false;
    recBox.className = 'rec live';
    recBox.innerHTML = `<span class="rec-dot"></span><span id="recTime" class="rec-time">0:00</span>
      <div class="rec-bars">${'<i></i>'.repeat(16)}</div>
      <span class="muted small">ліміт ${fmt(voiceMax())}</span>
      <button id="recStop" class="primary">Готово</button>
      <button id="recCancel" class="ghost icon" title="Викинути">✕</button>`;
    $('recStop').onclick = stopRec;
    $('recCancel').onclick = cancelRec;
  }

  function drawRecPreview(sec) {
    recBox.hidden = false;
    recBox.className = 'rec prev';
    recBox.innerHTML = `<span class="rec-mic">🎙</span><audio controls src="${recUrl}"></audio><span class="chip">${fmt(sec)}</span>
      <button id="recSend" class="primary">Закинути в чергу</button>
      <button id="recAgain" class="ghost">Ану ще раз</button>
      <button id="recDrop" class="ghost icon danger" title="Викинути">✕</button>`;
    $('recSend').onclick = (e) => busy(e.currentTarget, 'несу…', sendRec);
    $('recAgain').onclick = () => { closeRec(); startRec(); };
    $('recDrop').onclick = closeRec;
  }

  async function sendRec() {
    if (!recBlob) return;
    try {
      const r = await fetch('/api/voice', {
        method: 'POST',
        headers: { 'Content-Type': recBlob.type || 'application/octet-stream', 'X-Nick': encodeURIComponent(me.nick) },
        body: recBlob,
      });
      let data = null;
      try { data = await r.json(); } catch { /* без тіла */ }
      if (!r.ok) throw new Error((data && data.message) || `HTTP ${r.status}`);
      ok(data);
      closeRec();
    } catch (e) { fail(e); }
  }

  function closeRec() {
    clearInterval(recTimer);
    recTimer = 0;
    stopMeter();
    releaseMic();
    dropRecBlob();
    recorder = null;
    recBox.hidden = true;
    recBox.innerHTML = '';
  }
  function dropRecBlob() {
    if (recUrl) URL.revokeObjectURL(recUrl);
    recUrl = null;
    recBlob = null;
  }
  function releaseMic() {
    if (recStream) recStream.getTracks().forEach((t) => t.stop());   // гасне і червона крапка у вкладці
    recStream = null;
  }

  // Смужки рівня: видно, що мікрофон таки чує, а не пише тишу.
  function startMeter(stream) {
    try {
      recActx = new (window.AudioContext || window.webkitAudioContext)();
      recAnalyser = recActx.createAnalyser();
      recAnalyser.fftSize = 256;
      recActx.createMediaStreamSource(stream).connect(recAnalyser);
      const data = new Uint8Array(recAnalyser.frequencyBinCount);
      const step = () => {
        if (!recAnalyser) return;
        recAnalyser.getByteFrequencyData(data);
        recBox.querySelectorAll('.rec-bars i').forEach((b, i) => {
          b.style.transform = `scaleY(${Math.max(0.14, Math.min(1, (data[2 + i * 3] / 255) * 1.7))})`;
        });
        recRaf = requestAnimationFrame(step);
      };
      step();
    } catch { /* без смужок теж пишеться */ }
  }
  function stopMeter() {
    cancelAnimationFrame(recRaf);
    recRaf = 0;
    recAnalyser = null;
    try { if (recActx) recActx.close(); } catch { /* уже закритий */ }
    recActx = null;
  }

  $('micBtn').onclick = () => (recorder ? stopRec() : startRec());
  window.addEventListener('pagehide', closeRec);

  // ---------- послухати голосове до того, як воно піде в ефір ----------
  function playVoice(id) {
    const a = $('voiceAudio');
    if (a.dataset.id === id && !a.paused) { a.pause(); return; }
    a.dataset.id = id;
    a.src = `/api/voice/${encodeURIComponent(id)}.mp3`;
    a.play().catch((e) => toast('Халепа: не програлось — ' + e.message, 'err'));
  }
  function markVoiceButtons() {
    const a = $('voiceAudio');
    document.querySelectorAll('button.vplay').forEach((b) => {
      const on = b.dataset.id === a.dataset.id && !a.paused;
      b.textContent = on ? '⏸' : '▶';
      b.classList.toggle('active', on);
    });
  }
  function wireVoiceButtons(root) {
    root.querySelectorAll('button.vplay').forEach((b) => b.onclick = () => playVoice(b.dataset.id));
    markVoiceButtons();
  }
  ['play', 'pause', 'ended'].forEach((e) => $('voiceAudio').addEventListener(e, markVoiceButtons));

  // ---------- drop (or paste) a link anywhere on the page ----------
  const drop = $('drop');
  const dropTitle = drop.querySelector('.drop-title');
  const dropSub = drop.querySelector('.drop-sub');
  const dropUrl = drop.querySelector('.drop-url');
  let dragDepth = 0, dropTimer = null;
  // Кидки й вставки, за якими зараз стежить оверлей. Поки він показує «Закидаю…», нові посилання стають у ту
  // саму чергу закидань (раніше друге посилання в цей час просто губилось).
  let dropRun = null;              // { jobs: [...] }
  const hasText = (dt) => !!dt && [...(dt.types || [])].some((t) => t === 'text/uri-list' || t === 'text/plain' || t === 'text' || t === 'Text');

  function showDrop(kind, title, sub, url) {
    clearTimeout(dropTimer);
    drop.className = 'drop ' + kind;
    dropTitle.textContent = title;
    dropSub.textContent = sub || '';
    dropUrl.textContent = url || '';
    dropUrl.hidden = !url;
    drop.hidden = false;
  }
  function hideDrop(delay) {
    clearTimeout(dropTimer);
    dropTimer = setTimeout(() => {
      drop.classList.add('leaving');
      setTimeout(() => { drop.hidden = true; drop.className = 'drop'; }, 250);
    }, delay || 0);
  }
  document.addEventListener('dragenter', (e) => {
    if (!hasText(e.dataTransfer)) return;
    e.preventDefault();
    if (dragDepth++ === 0 && !dropRun) showDrop('over', 'Кидай сюди', 'YouTube, YT Music, Spotify — трек, альбом чи плейлист');
  });
  document.addEventListener('dragover', (e) => {
    if (!hasText(e.dataTransfer)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  });
  document.addEventListener('dragleave', (e) => {
    if (!hasText(e.dataTransfer)) return;
    if (--dragDepth <= 0) { dragDepth = 0; if (!dropRun) hideDrop(); }
  });
  document.addEventListener('drop', (e) => {
    if (!hasText(e.dataTransfer)) return;
    e.preventDefault();
    dragDepth = 0;
    const dt = e.dataTransfer;
    // кілька треків, перетягнутих разом (скажімо, зі Spotify), приходять кожен своїм рядком
    const uris = (dt.getData('text/uri-list') || '').split('\n').map((s) => s.trim()).filter((s) => s && !s.startsWith('#'));
    dropText((uris.join('\n') || dt.getData('text/plain') || dt.getData('text') || '').trim());
  });
  document.addEventListener('paste', (e) => {
    const tag = (e.target.tagName || '').toLowerCase();
    if (tag === 'input' || tag === 'textarea' || e.target.isContentEditable) return;
    const text = (e.clipboardData?.getData('text/plain') || '').trim();
    if (splitLinks(text).length) { e.preventDefault(); dropText(text); }
  });
  function dropText(text) {
    const links = splitLinks(text);
    if (!links.length) {
      // plain words are a search, not a link
      if (!dropRun) hideDrop();
      if (!text) return;
      q.value = text.slice(0, 120);
      q.focus();
      search(q.value);
      return;
    }
    submitLinks(links, 'drop', dropRun || (dropRun = { jobs: [] }));
    drawDropProgress();
  }
  /// Оверлей за чергою закидань: «Закидаю…» (і скільки ще чекає), поки з кинутого щось не скінчилось, тоді підсумок.
  function drawDropProgress() {
    const run = dropRun;
    if (!run) return;
    const jobs = run.jobs.filter((j) => j.state !== 'handoff');
    if (!jobs.length) return;
    const live = jobs.filter((j) => j.state === 'wait' || j.state === 'run');
    if (live.length) {
      const cur = live.find((j) => j.state === 'run') || live[0];
      const more = live.length - 1;
      showDrop('busy', cur.kind === 'album' ? 'Відкриваю…' : 'Закидаю…',
        (cur.kind === 'album' ? 'тягну трекліст, це кілька секунд' : 'розбираю посилання, це може зайняти кілька секунд')
        + (more ? ` · ще ${more} чекає` : ''), cur.label);
      return;
    }
    dropRun = null;
    const done = jobs.filter((j) => j.state === 'ok');
    const bad = jobs.filter((j) => j.state === 'err');
    if (!bad.length) {
      const one = done.length === 1 ? done[0] : null;
      // Префікс «Закинуто:» у відповіді сервера ріжемо разом із вигуком перед ним («Є! Закинуто: …»), якщо він там буде.
      showDrop('done', one && one.kind === 'album' ? 'Лови трекліст' : 'Є! Закинуто',
        one ? String(one.msg).replace(/^(?:\S+!\s+)?Закинуто:\s*/, '').replace(/ — трекліст під пошуком$/, '') : `усі ${done.length} по черзі`, '');
      hideDrop(1600);
    } else {
      showDrop('err', done.length ? `Закинуто ${done.length} з ${jobs.length}` : 'От халепа — не вийшло', bad[0].msg, bad[0].label);
      hideDrop(3500);
    }
  }

  // ---------- library: history / likes / playlists / stats ----------
  $('libTabs').querySelectorAll('button').forEach((b) => b.onclick = () => go('#lib/' + b.dataset.tab));
  /// Шукаємо по тому, що вже на екрані: сервер тут ні до чого, і відповідь миттєва.
  function filterLib() {
    const box = $('lib');
    const want = $('libFind').value.trim().toLowerCase();
    const rows = box.querySelectorAll('.list > li:not(.lday)');
    // підписи днів під час пошуку лише заважають: знайдене з різних днів стоїть підряд
    box.querySelectorAll('.list > li.lday').forEach((li) => { li.hidden = !!want; });
    let shown = 0;
    const no = /^#\d+$/.test(want) ? want : '';   // «#2» — саме записка №2, а не ще й #20…#29
    rows.forEach((li) => {
      const fbNoEl = no && li.querySelector('.fbno');
      const hit = !want || (fbNoEl ? fbNoEl.textContent === no : li.textContent.toLowerCase().includes(want));
      li.hidden = !hit;
      if (hit) shown++;
    });
    let none = box.querySelector('.findnone');
    if (want && rows.length && !shown) {
      if (!none) { none = document.createElement('div'); none.className = 'empty findnone'; box.appendChild(none); }
      none.textContent = `Нічого схожого на «${$('libFind').value.trim()}» тут нема.`;
    } else if (none) none.remove();
  }
  $('libFind').addEventListener('input', filterLib);

  const trackRow = (t, right, extra) => `<li>
      ${cover(t)}
      <div style="min-width:0"><div class="t ${extra?.skipped ? 'skipped' : ''}">${esc(t.title)} <span class="muted">· ${esc(t.artist)}</span></div><div class="r">${right}</div></div>
      <div class="btns">${voiceBtn(t)}<button class="q" data-id="${esc(t.id)}" title="Закинути в чергу">в чергу</button><button class="ghost pl" data-id="${esc(t.id)}" data-title="${esc(t.title)}" title="Зберегти в плейлист">📂＋</button></div>
    </li>`;
  function wireRows(root) {
    root.querySelectorAll('button.q').forEach((b) => b.onclick = (e) => busy(e.currentTarget, '…', () => queueTrack(b.dataset.id)));
    root.querySelectorAll('button.pl').forEach((b) => b.onclick = () => openPlaylistPicker(b.dataset.id, b.dataset.title));
    wireVoiceButtons(root);
  }
  async function loadLib() {
    await drawLib();
    filterLib();
  }
  // «Що вже було»: дні підписані, як у балачках («13:16» — а якого дня?), «Мої» — лише те, що закинув я,
  // «Показати ще» тягне старіше пачками (сервер уміє before=<id>).
  let histWho = (() => { try { return localStorage.getItem('histWho') === 'mine' ? 'mine' : 'all'; } catch { return 'all'; } })();
  const HIST_N = 80;
  function histRows(list) {
    let day = null;
    return list.map((h) => {
      const d = dayKey(h.startedAt);
      const sep = d !== day ? `<li class="lday"><span>${esc(dayLabel(h.startedAt))}</span></li>` : '';
      day = d;
      const who = h.source === 'autodj' ? esc(dj()) : h.requestedBy ? nickHtml(h.requestedBy, 'rnick') : '';
      return sep + trackRow(h.track,
        `${who}${h.via === 'suggestion' ? ' · порада' : ''}${h.likes ? ' · ❤' + h.likes : ''}${h.skipped ? ' · скіп' : ''} · ${tm(h.startedAt)}`,
        { skipped: h.skipped });
    }).join('');
  }
  async function renderHistory(more) {
    const box = $('lib');
    const ul = box.querySelector('ul.hist');
    const last = more && ul ? +(ul.dataset.last || 0) : 0;
    const qs = `n=${HIST_N}` + (histWho === 'mine' && me.nick ? '&by=' + encodeURIComponent(me.nick) : '') + (last ? '&before=' + last : '');
    let list = await api('GET', '/api/history?' + qs);
    // Сервер, що ще не знає by, віддасть усе — тоді «Мої» відбираємо тут, у тому, що прийшло.
    if (histWho === 'mine') list = list.filter((h) => h.source === 'user' && sameNick(h.requestedBy, me.nick));
    const moreBtn = (n) => (n >= HIST_N || (histWho === 'mine' && list.length) ? '<button class="ghost histmore" type="button">Гортнути ще</button>' : '');
    if (more && ul) {
      ul.insertAdjacentHTML('beforeend', histRows(list));
      // роздільник дня на шві двох пачок: той самий день — другий заголовок зайвий
      const seps = [...ul.querySelectorAll('li.lday')];
      for (let i = 1; i < seps.length; i++) if (seps[i].textContent === seps[i - 1].textContent) seps[i].remove();
      if (list.length) ul.dataset.last = String(list[list.length - 1].id);
      box.querySelector('.histmore')?.remove();
      if (list.length) ul.insertAdjacentHTML('afterend', moreBtn(list.length));
    } else {
      const seg = `<div class="tabs seg">${[['all', 'Усі'], ['mine', 'Мої']].map(([v, l]) =>
        `<button data-v="${v}" class="${histWho === v ? 'on' : ''}">${l}</button>`).join('')}</div>`;
      const empty = histWho === 'mine'
        ? 'Від тебе тут ще жодної пісні. Закинь щось в «Ефірі» — і тут почне збиратись твоє.'
        : 'Ще нічого не грало. Закинь першу пісню — і тут почне збиратись історія.';
      box.innerHTML = seg + `<ul class="list hist" data-last="${list.length ? list[list.length - 1].id : ''}">${histRows(list) || `<li class="empty glek">${empty}</li>`}</ul>` + moreBtn(list.length);
      box.querySelectorAll('.seg button').forEach((b) => b.onclick = () => {
        histWho = b.dataset.v;
        try { localStorage.setItem('histWho', histWho); } catch { /* приватне вікно */ }
        loadLib();
      });
    }
    wireRows(box);
    const mb = box.querySelector('.histmore');
    if (mb) mb.onclick = (e) => busy(e.currentTarget, 'тягну…', () => renderHistory(true).then(filterLib));
  }
  async function drawLib() {
    const box = $('lib');
    try {
      if (libTab === 'history') {
        await renderHistory(false);
        return;
      } else if (libTab === 'likes') {
        await renderLikes();
        return;
      } else if (libTab === 'playlists') {
        await renderPlaylists();
        return;
      } else if (libTab === 'bans') {
        await renderBans();
        return;
      } else if (libTab === 'ads') {
        await renderAds();
        return;
      } else if (libTab === 'feedback') {
        await renderFeedbackAdmin();
        return;
      } else if (libTab === 'photos') {
        await HLavka.adminPhotos(box);   // «Своя фотка» з Лавки: переглянути й зняти (lavka.js)
        return;
      } else if (libTab === 'mod') {
        await HModer.renderTab(box);     // «🛡 Модерація»: файли всім, 🐢, 📌, хто обмежений, журнал (web/moder.js)
        return;
      }
      wireRows(box);
    } catch (e) { box.innerHTML = `<div class="empty">Ой-йой: ${esc(e.message)}</div>`; }
  }

  // ---------- «💡 Пропозиції й баги» — вкладка розробника (адміна) ----------
  // Усі записки з перепискою: відповідь під запискою бачить автор (число на його 💡). Записка, де людина відписала,
  // стоїть згори з позначкою «↩ відповідь» за будь-якого фільтра: показали — отже, розробник її бачив, і сервер
  // рахує прочитаною, а позначка тримається до першої дії з запискою (відповідь, стан) чи до F5. Перша відповідь на
  // нову записку робить її «переглянутою» (Feedback.Say), тож у фільтрі «нові» лишається лише непрочитане — але та,
  // з якою щойно щось зробив, лишається на місці до зміни фільтра: видно, що відповідь лягла в переписку.
  let fbFilter = (() => { try { return localStorage.getItem('fbFilter') || 'new'; } catch { return 'new'; } })();
  const fbHot = new Set();        // записки з відповіддю людини, показаною в цій вкладці
  const fbDevFresh = new Set();   // повідомлення людей, нові на момент показу
  const fbKeep = new Set();       // записки, з якими щойно щось зробив: не зникають із фільтра до його зміни
  let fbWaitRead = [];            // показано, поки вкладка браузера була позаду: прочитаємо, щойно повернуть
  /// «Chrome 140 · Windows» — з рядка браузера досить цього; повний — у підказці.
  function shortUa(ua) {
    const s = String(ua || '');
    const b = /Edg\/(\d+)/.exec(s) ? 'Edge ' + /Edg\/(\d+)/.exec(s)[1] : /Firefox\/(\d+)/.exec(s) ? 'Firefox ' + /Firefox\/(\d+)/.exec(s)[1]
      : /Chrome\/(\d+)/.exec(s) ? 'Chrome ' + /Chrome\/(\d+)/.exec(s)[1] : /Safari\//.test(s) ? 'Safari' : '';
    const p = /Android/.test(s) ? 'Android' : /iPhone|iPad/.test(s) ? 'iOS' : /Windows/.test(s) ? 'Windows' : /Mac OS X/.test(s) ? 'Mac' : /Linux/.test(s) ? 'Linux' : '';
    return [b, p].filter(Boolean).join(' · ');
  }
  function readAsDev() {
    const ids = fbWaitRead;
    fbWaitRead = [];
    if (ids.length) api('POST', '/api/feedback/read', { ids }).then((r) => paintDevCount(r && r.waiting)).catch(() => {});
  }
  async function renderFeedbackAdmin() {
    const box = $('lib');
    if (me.role !== 'admin') { box.innerHTML = '<div class="empty">Це бачить лише розробник</div>'; return; }
    // Усі разом (до 300), а фільтр — тут: записка з відповіддю мусить лишатись на виду, хоч би її стан і не підходив.
    const r = await api('GET', '/api/feedback');
    const all = r.items || [];
    paintDevCount(r.waiting);
    for (const x of all) {
      if (x.unread) { fbHot.add(x.id); if (!fbWaitRead.includes(x.id)) fbWaitRead.push(x.id); }
      for (const m of x.msgs || []) if (m.fresh) fbDevFresh.add(m.id);
    }
    const c = r.counts || {};
    const total = Object.values(c).reduce((a, b) => a + b, 0);
    const seg = [['new', 'нові'], ['seen', 'переглянуті'], ['planned', 'у планах'], ['done', 'зроблені'], ['nope', 'не буде'], ['all', 'усі']]
      .map(([v, l]) => `<button data-v="${v}" class="${fbFilter === v ? 'on' : ''}">${l} · ${v === 'all' ? total : (c[v] || 0)}</button>`).join('');
    const shown = all.filter((x) => fbHot.has(x.id) || fbKeep.has(x.id) || fbFilter === 'all' || x.status === fbFilter)
      .sort((a, b) => (Number(fbHot.has(b.id)) - Number(fbHot.has(a.id))) || (b.id - a.id));
    const row = (x) => {
      const hot = fbHot.has(x.id);
      const msgs = x.msgs || [];
      return `<li class="fbi fb-${esc(x.kind)}${hot ? ' hot' : ''}" data-id="${x.id}">
        <div class="fbi-head">${FB_ICON[x.kind] || '💬'} ${fbNo(x.id)} ${nickHtml(x.nick, 'rnick')}<span class="muted small">${esc(dayTime(x.at))}</span>${hot ? '<span class="chip warn fbi-hot">↩ відповідь</span>' : ''}
          <select class="fbi-st" aria-label="Стан">${Object.entries(FB_STATUS).map(([k, [l]]) => `<option value="${k}"${k === x.status ? ' selected' : ''}>${l}</option>`).join('')}</select></div>
        <div class="fbi-text">${esc(x.text)}</div>
        <div class="fbi-ctx muted small">${x.place ? '📍 ' + esc(x.place) : ''}${x.screen ? ' · ' + esc(x.screen) : ''}${x.ua ? ` · <span title="${esc(x.ua)}">${esc(shortUa(x.ua))}</span>` : ''}</div>
        ${msgs.length ? `<div class="fbt">${msgs.map((m) => fbBubble(m, true, fbDevFresh)).join('')}</div>` : ''}
        ${fbSayBox('Відповісти… (Enter)')}
      </li>`;
    };
    const empty = fbFilter === 'new' ? 'Нових записок нема — усе прочитано.' : 'Тут поки порожньо.';
    const drafts = fbDrafts(box);
    box.innerHTML = `<div class="tabs seg fbseg">${seg}</div><ul class="list fblist">${shown.map(row).join('') || `<li class="empty glek">${empty}</li>`}</ul>`;
    fbRestore(box, drafts);
    box.querySelectorAll('.fbseg [data-v]').forEach((b) => b.onclick = () => {
      fbFilter = b.dataset.v;
      fbKeep.clear();
      try { localStorage.setItem('fbFilter', fbFilter); } catch { /* приватне вікно */ }
      loadLib();
    });
    // Відповів чи переставив стан — з запискою розібрались, позначка «↩ відповідь» їй більше ні до чого.
    const done = (id) => { fbHot.delete(+id); fbKeep.add(+id); loadFeedbackCount(); return loadLib(); };
    box.querySelectorAll('.fbi').forEach((li) => {
      li.querySelector('.fbi-st').onchange = async (e) => {
        try { await api('PATCH', `/api/feedback/${li.dataset.id}`, { status: e.target.value }); await done(li.dataset.id); } catch (err) { fail(err); }
      };
    });
    fbWireSay(box, async (id, text, t) => {
      try {
        ok(await api('POST', `/api/feedback/${id}/msg`, { text }));
        t.value = '';
        await done(id);
      } catch (err) { fail(err); }
    });
    if (!document.hidden) readAsDev();
  }
  // улюблене: типово — моє (згори те, що лайкнуто останнім), «Усі» — спільний список; вибір пам'ятаємо
  let likesWho = 'mine';
  try { if (localStorage.getItem('likesWho') === 'all') likesWho = 'all'; } catch { /* приватне вікно */ }
  async function renderLikes() {
    const box = $('lib');
    const list = await api('GET', '/api/likes');
    const mine = list
      .map((l) => ({ ...l, my: l.likes.find((x) => sameNick(x.nick, me.nick)) }))
      .filter((l) => l.my)
      .sort((a, b) => new Date(b.my.at) - new Date(a.my.at));
    const rows = likesWho === 'mine'
      ? mine.map((l) => {
        const others = l.likes.filter((x) => x !== l.my).map((x) => x.nick);
        return trackRow(l.track, `❤ ${dayTime(l.my.at)}${others.length ? ` · і ${esc(others.join(', '))}` : ''}`);
      })
      : list.map((l) => trackRow(l.track, `❤ ${esc(l.likes.map((x) => x.nick).join(', '))}`));
    const empty = likesWho === 'mine' && list.length
      ? 'Твоїх вподобайок тут ще нема: тисни ❤ під треком в ефірі — і пісня осяде тут. Що люблять інші — перемкни на «Усі».'
      : 'Ще жодної вподобайки. Тисни ❤ під треком в ефірі — і пісня осяде тут.';
    box.innerHTML = `<div class="tabs seg">${[['mine', `Мої · ${mine.length}`], ['all', `Усі · ${list.length}`]].map(([v, label]) =>
      `<button data-v="${v}" class="${likesWho === v ? 'on' : ''}">${label}</button>`).join('')}</div>` +
      `<ul class="list">${rows.join('') || `<li class="empty glek">${empty}</li>`}</ul>`;
    box.querySelectorAll('.seg button').forEach((b) => b.onclick = () => {
      likesWho = b.dataset.v;
      try { localStorage.setItem('likesWho', likesWho); } catch { /* приватне вікно */ }
      loadLib();
    });
    wireRows(box);
  }

  // бан-лист: хто, коли й за скільки забанив; адмін розбанює безкоштовно, решта викуповує за черепки
  const dayTime = (iso) => new Date(iso).toLocaleString('uk-UA', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
  async function renderBans() {
    const box = $('lib');
    const r = await api('GET', '/api/bans');
    const admin = me.role === 'admin';
    const how = [
      'Забанене не грає, не стає в чергу й не потрапляє в поради.',
      r.banPrice ? `Забанити те, що зараз в ефірі, — ${r.banPrice} 🏺, кнопка «🚫 бан» під треком.` : '',
      admin ? 'Ти адмін: банити й розбанювати безкоштовно.'
        : r.unbanPrice ? `Викупити трек із бану — ${r.unbanPrice} 🏺. У тебе ${r.balance} 🏺.` : 'Розбанює лише адмін.',
    ].filter(Boolean).join(' ');
    const unbanBtn = (t) => admin
      ? `<button class="ghost unban" data-id="${esc(t.id)}" data-title="${esc(t.title)}" title="Зняти бан">розбанити</button>`
      : r.unbanPrice ? `<button class="ghost unban" data-id="${esc(t.id)}" data-title="${esc(t.title)}" title="Викупити з бану за ${r.unbanPrice} черепків">викупити · ${r.unbanPrice} 🏺</button>` : '';
    const row = (b) => `<li>
        ${cover(b.track)}
        <div style="min-width:0"><div class="t">${esc(b.track.title)}${b.track.artist ? ` <span class="muted">· ${esc(b.track.artist)}</span>` : ''}</div>
          <div class="r">бан від ${esc(fromNick(b.by || '?'))}${b.price ? ` за ${b.price} 🏺` : ''} · ${dayTime(b.createdAt)}</div></div>
        <div class="btns">${b.track.sourceUrl && !isVoice(b.track) ? `<a class="chip" href="${esc(b.track.sourceUrl)}" target="_blank" rel="noopener" title="Що це було">↗</a>` : ''}${unbanBtn(b.track)}</div>
      </li>`;
    box.innerHTML = `<div class="muted small" style="margin-bottom:8px">${how}</div>` +
      `<ul class="list">${r.items.map(row).join('') || '<li class="empty glek">Бан-лист порожній: усе, що грало, всіх влаштувало.</li>'}</ul>`;
    box.querySelectorAll('button.unban').forEach((b) => b.onclick = (e) => {
      const ask = admin ? `Розбанити «${b.dataset.title}»?` : `Викупити «${b.dataset.title}» з бану за ${r.unbanPrice} 🏺?`;
      if (!confirm(ask)) return;
      busy(e.currentTarget, '…', () => api('DELETE', `/api/ban/${encodeURIComponent(b.dataset.id)}`)
        .then((res) => { ok(res); return loadLib(); }).catch(fail));
    });
  }

  // ---------- реклама: бібліотека, ротація, частота (лише адмін) ----------
  const ago = (iso) => {
    if (!iso) return 'ще не грала';
    const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
    return m < 1 ? 'щойно' : m < 60 ? `${m} хв тому` : m < 1440 ? `${Math.round(m / 60)} год тому` : dayTime(iso);
  };
  const times = (n) => `${n} ${n % 100 >= 11 && n % 100 <= 14 ? 'разів' : n % 10 === 1 ? 'раз' : n % 10 >= 2 && n % 10 <= 4 ? 'рази' : 'разів'}`;
  async function renderAds() {
    const box = $('lib');
    if (me.role !== 'admin') { box.innerHTML = '<div class="empty">Це бачить лише адмін</div>'; return; }
    const r = await api('GET', '/api/ads/library');
    const on = r.items.filter((a) => a.enabled && !a.missing).length;
    // Конкурсу реклами більше нема (26.09.2026), а з ним і запасної: порожня ротація — просто без реклами.
    const fb = 'реклами в ефірі не буде';
    const head = `<div class="ads-head">
        <div class="ads-row"><b>У ротації ${on} з ${r.items.length}</b>
          <span class="muted small">випадково без повторів · від останньої реклами ${r.since} тр.${r.jingle ? '' : ' · ⚠ джингл вимкнено в Ad:Jingle'}</span></div>
        <div class="ads-row">
          <span>Раз на</span><input id="adsEvery" type="number" min="1" max="100" value="${r.everyTracks}"><span>тр., не частіше ніж раз на</span>
          <input id="adsMins" type="number" min="0" max="600" value="${r.minMinutes}"><span>хв</span>
          <button class="ghost" id="adsSaveEvery">Зберегти</button>
        </div>
        <div class="ads-row" title="Запаска — коли нове не вантажиться і в ефірі крутиться знайоме з кешу">
          <span>У запасці раз на</span><input id="adsSpareEvery" type="number" min="1" max="100" value="${r.spareEveryTracks}"><span>тр., не частіше ніж раз на</span>
          <input id="adsSpareMins" type="number" min="0" max="600" value="${r.spareMinMinutes}"><span>хв</span>
          <button class="ghost" id="adsSaveSpare">Зберегти</button>
        </div>
        <div class="ads-row">
          <button class="primary" id="adsNow" title="Наступна реклама з колоди стане в чергу">📻 Наступну в чергу</button>
          <button class="ghost" id="adsAllOn">Усі в ротацію</button>
          <button class="ghost" id="adsAllOff">Вимкнути ротацію</button>
        </div>
        <div class="muted small">За прослухану рекламу слухачам з увімкненим плеєром +${r.reward.amount} 🏺 (до ${r.reward.dailyCap} на день).
          Коли в ротації порожньо, ${fb}.</div>
        <form class="ads-row" id="adsUpload">
          <input type="file" accept="audio/*,video/webm,.mp3,.wav,.ogg,.m4a" required>
          <input type="text" maxlength="60" placeholder="назва реклами" autocomplete="off">
          <button class="primary" type="submit">Залити</button>
          <span class="muted small">до ${r.maxMb} МБ</span>
        </form>
      </div>`;
    const row = (a) => `<li class="${a.enabled ? '' : 'off'}">
        ${cover({ id: a.trackId })}
        <div style="min-width:0"><div class="t"><span class="ads-title" data-id="${a.id}" title="Перейменувати">${esc(a.title)}</span></div>
          <div class="r">${fmt(a.seconds)} · грала ${times(a.plays)} · ${ago(a.lastPlayedAt)}${a.missing ? ' · <span class="chip err">файл зник</span>' : ''}</div></div>
        <div class="btns">
          ${voiceBtn({ id: a.trackId })}
          <label class="ads-switch" title="${a.enabled ? 'У ротації' : 'Не в ротації'}"><input type="checkbox" data-id="${a.id}" ${a.enabled ? 'checked' : ''}> ротація</label>
          <button class="ghost" data-now="${a.id}" title="Саме цю — в чергу">📻</button>
          <button class="ghost danger" data-del="${a.id}" data-title="${esc(a.title)}" title="Видалити">✕</button>
        </div>
      </li>`;
    box.innerHTML = '<div id="adsLive" class="la-admin-box"></div>' + head + `<ul class="list ads-list">${r.items.slice().reverse().map(row).join('') || '<li class="empty">Бібліотека порожня. Залий перший файл вище.</li>'}</ul>`;

    const again = () => loadLib();
    $('adsSaveEvery').onclick = (e) => busy(e.currentTarget, '…', () => api('POST', '/api/ads/air/every',
      { everyTracks: +$('adsEvery').value, minMinutes: +$('adsMins').value }).then(ok).then(again).catch(fail));
    $('adsSaveSpare').onclick = (e) => busy(e.currentTarget, '…', () => api('POST', '/api/ads/air/spare-every',
      { everyTracks: +$('adsSpareEvery').value, minMinutes: +$('adsSpareMins').value }).then(ok).then(again).catch(fail));
    $('adsNow').onclick = (e) => busy(e.currentTarget, 'закидаю…', () => api('POST', '/api/ads/air/now').then(ok).catch(fail));
    $('adsAllOn').onclick = (e) => busy(e.currentTarget, '…', () => api('POST', '/api/ads/library/all', { enabled: true }).then(ok).then(again).catch(fail));
    $('adsAllOff').onclick = (e) => confirm('Вимкнути всі реклами з ротації?') && busy(e.currentTarget, '…',
      () => api('POST', '/api/ads/library/all', { enabled: false }).then(ok).then(again).catch(fail));
    $('adsUpload').onsubmit = (e) => {
      e.preventDefault();
      const [file, name] = e.target.querySelectorAll('input');
      const f = file.files[0];
      if (!f) return;
      const title = name.value.trim() || f.name.replace(/\.[^.]+$/, '');
      busy(e.target.querySelector('button'), 'заливаю…', async () => {
        const res = await fetch('/api/ads/library?title=' + encodeURIComponent(title), {
          method: 'POST', body: f,
          headers: { 'X-Nick': encodeURIComponent(me.nick), 'Content-Type': f.type || 'application/octet-stream' },
        });
        const j = await res.json().catch(() => ({ message: 'сервер відповів ' + res.status }));
        if (!res.ok) throw new Error(j.message);
        ok(j);
        await again();
      }).catch(fail);
    };
    box.querySelectorAll('.ads-switch input').forEach((c) => c.onchange = () =>
      api('PATCH', `/api/ads/library/${c.dataset.id}`, { enabled: c.checked }).then(again).catch((e) => { c.checked = !c.checked; fail(e); }));
    box.querySelectorAll('[data-now]').forEach((b) => b.onclick = (e) =>
      busy(e.currentTarget, '…', () => api('POST', `/api/ads/library/${b.dataset.now}/now`).then(ok).catch(fail)));
    box.querySelectorAll('[data-del]').forEach((b) => b.onclick = (e) => confirm(`Видалити «${b.dataset.title}» назавжди?`) &&
      busy(e.currentTarget, '…', () => api('DELETE', `/api/ads/library/${b.dataset.del}`).then(ok).then(again).catch(fail)));
    box.querySelectorAll('.ads-title').forEach((t) => t.onclick = () => {
      const name = prompt('Нова назва реклами', t.textContent);
      if (name == null || !name.trim() || name.trim() === t.textContent) return;
      api('PATCH', `/api/ads/library/${t.dataset.id}`, { title: name.trim() }).then(ok).then(again).catch(fail);
    });
    wireVoiceButtons(box);
    if (window.HLiveAds) await HLiveAds.admin($('adsLive'));   // блок «Жива реклама» (web/liveads.js)
  }

  // ---------- імпорт плейлиста з посилання ----------
  // Раніше це вміло лише поле «Закинути пісню», і про це ніхто не здогадувався. Тепер — окремий блок у «Плейлистах»:
  // посилання → трекліст із галочками → назва (або дописати в наявний) → зберегти чи одразу в чергу.
  let imp = null;                  // { url, data, off: Set(id), busy }
  const IMP_HELP = 'Встав посилання на плейлист чи альбом — покажу трекліст. Збережеш його своїм плейлистом тут або одразу закинеш у чергу. '
    + 'Приватний плейлист не відкриється — зроби його доступним за посиланням. З довгих плейлистів беремо перші 100 треків.';
  const impName = (a) => {
    const lead = String(a.artist || '').split(/,\s|\s&\s|\sі\s/)[0].trim();
    const name = a.kind === 'album' && lead ? `${lead} — ${a.title}` : a.title;
    return name.length <= 40 ? name : name.slice(0, 39).trimEnd() + '…';
  };
  function impBlock() {
    return `<section class="plimp">
        <div class="plimp-head"><b>📥 Перенести плейлист звідкись</b><span class="muted small">Spotify · YouTube Music · YouTube</span></div>
        <div class="muted small">${esc(IMP_HELP)}</div>
        <form class="plimp-row" id="plImpForm">
          <input type="text" inputmode="url" placeholder="https://open.spotify.com/playlist/…  або  music.youtube.com/playlist?list=…" autocomplete="off" value="${imp ? esc(imp.url) : ''}">
          <button class="primary" type="submit">Відкрити</button>
        </form>
        <div id="plImpView"></div>
      </section>`;
  }
  function drawImport(playlists) {
    const view = $('plImpView');
    if (!view) return;
    if (!imp) { view.innerHTML = ''; return; }
    if (imp.busy) { view.innerHTML = '<div class="muted small plimp-wait"><span class="spin"></span> тягну трекліст і шукаю пісні в YouTube Music — це кілька секунд…</div>'; return; }
    if (imp.error) { view.innerHTML = `<div class="plimp-err">${esc(imp.error)}</div>`; return; }
    const a = imp.data;
    const found = a.tracks.filter((t) => t.match);
    const picked = found.filter((t) => !imp.off.has(t.match.id));
    const missing = a.tracks.length - found.length;
    const src = a.source === 'spotify' ? 'Spotify' : 'YouTube Music';
    const sub = [a.artist, a.year, tracksN(a.tracks.length), `${Math.max(1, Math.round((a.durationSec || 0) / 60))} хв`].filter(Boolean).join(' · ');
    const lead = String(a.artist || '').split(/,\s|\s&\s|\sі\s/)[0].trim().toLowerCase();
    const row = (t) => {
      const by = t.artist && (a.kind !== 'album' || !lead || !t.artist.toLowerCase().includes(lead)) ? ` <span class="muted">· ${esc(t.artist)}</span>` : '';
      if (!t.match) return `<li class="miss" title="Не знайшлось у YouTube Music — пропущу"><span class="n"><span>${t.n}</span></span><div class="t">${esc(t.title)}${by}</div><span class="d">${fmt(t.durationSec)}</span><span class="chip">нема</span></li>`;
      const off = imp.off.has(t.match.id);
      return `<li class="${off ? 'off' : ''}" data-id="${esc(t.match.id)}"><label class="n"><input type="checkbox" ${off ? '' : 'checked'}><span>${t.n}</span></label>`
        + `<div class="t">${esc(t.title)}${by}</div><span class="d">${fmt(t.match.durationSec || t.durationSec)}</span></li>`;
    };
    const to = imp.to || '';
    view.innerHTML = `<div class="album imp">
        <div class="album-head">
          ${a.thumbUrl ? `<img src="${esc(a.thumbUrl)}" alt="">` : '<div class="noimg">💿</div>'}
          <div class="album-meta">
            <div class="album-kind">${a.kind === 'album' ? 'Альбом' : 'Плейлист'} · ${src}</div>
            <div class="album-title" title="${esc(a.title)}">${esc(a.title)}</div>
            <div class="album-sub">${esc(sub)}</div>
          </div>
          <button class="ghost icon imp-x" type="button" title="Закрити">✕</button>
        </div>
        <div class="plimp-save">
          <select id="plImpTo" title="Куди зберегти">
            <option value="">у новий плейлист</option>
            ${(playlists || []).map((p) => `<option value="${p.id}" ${String(p.id) === to ? 'selected' : ''}>дописати в «${esc(p.name)}»</option>`).join('')}
          </select>
          <input id="plImpName" type="text" maxlength="40" placeholder="назва плейлиста" value="${esc(imp.name != null ? imp.name : impName(a))}" ${to ? 'hidden' : ''}>
        </div>
        <div class="album-btns">
          <button class="primary" data-go="save" ${picked.length ? '' : 'disabled'}>📂 Зберегти · ${picked.length}</button>
          <button data-go="order" ${picked.length ? '' : 'disabled'} title="По порядку">▶ У чергу</button>
          <button class="ghost" data-go="shuffle" ${picked.length > 1 ? '' : 'disabled'}>🔀 Упереміш</button>
          <a class="chip album-src" href="${esc(a.url)}" target="_blank" rel="noopener" title="Відкрити в ${src}">↗ ${src}</a>
        </div>
        <ol class="album-tracks">${a.tracks.map(row).join('')}</ol>
        ${missing ? `<div class="album-foot">${missing === a.tracks.length ? 'Жоден трек' : plural(missing, 'трек', 'треки', 'треків')} не знайшлось у YouTube Music — ${missing === a.tracks.length ? 'зберігати нема чого' : 'їх пропущу'}.</div>` : ''}
        ${a.kind === 'playlist' && a.tracks.length >= 100 ? '<div class="album-foot">Тут перші 100 треків плейлиста: більше за раз не віддають.</div>' : ''}
      </div>`;
    view.querySelector('.imp-x').onclick = () => { imp = null; drawImport(playlists); const f = $('plImpForm'); if (f) f.querySelector('input').value = ''; };
    view.querySelectorAll('.album-tracks li[data-id] input').forEach((c) => c.onchange = () => {
      const id = c.closest('li').dataset.id;
      if (c.checked) imp.off.delete(id); else imp.off.add(id);
      imp.name = $('plImpName').value;
      drawImport(playlists);
    });
    $('plImpTo').onchange = (e) => { imp.to = e.target.value; imp.name = $('plImpName').value; drawImport(playlists); };
    const ids = () => (picked.length === found.length ? null : picked.map((t) => t.match.id));
    view.querySelector('[data-go="save"]').onclick = (e) => busy(e.currentTarget, 'зберігаю…', async () => {
      const body = { url: imp.url, ids: ids() };
      if (imp.to) body.playlistId = +imp.to; else body.name = $('plImpName').value.trim() || impName(a);
      try { ok(await api('POST', '/api/album/playlist', body)); imp = null; renderPlaylists(); } catch (err) { fail(err); }
    });
    view.querySelectorAll('[data-go="order"], [data-go="shuffle"]').forEach((b) => b.onclick = (e) => busy(e.currentTarget, 'закидаю…', async () => {
      try { ok(await api('POST', '/api/album/queue', { url: imp.url, shuffle: b.dataset.go === 'shuffle', ids: ids() })); } catch (err) { fail(err); }
    }));
  }
  async function openImport(url, playlists) {
    const links = splitLinks(url);
    const link = links[0] || url.trim();
    if (!link) return;
    imp = { url: link, data: null, off: new Set(), busy: true, to: '', name: null };
    drawImport(playlists);
    try {
      imp.data = await api('GET', '/api/album?url=' + encodeURIComponent(link));
    } catch (e) {
      imp.error = e.data && e.data.notAlbum
        ? 'Це посилання на один трек, а не на плейлист. Його можна закинути в «Ефірі» або зберегти кнопкою 📂＋ під треком.'
        : e.message;
    }
    if (imp) { imp.busy = false; drawImport(playlists); }
  }

  const openPl = new Set();
  async function renderPlaylists() {
    const box = $('lib');
    const list = await api('GET', '/api/playlists');
    box.innerHTML = impBlock() + `<form class="pl-new" id="plCreate"><input type="text" maxlength="40" placeholder="Або порожній: назва нового плейлиста…" autocomplete="off"><button type="submit">Створити</button></form>` +
      (list.map((p) => `<div class="pl" data-id="${p.id}">
          ${p.thumbUrl ? `<img src="${esc(p.thumbUrl)}" alt="">` : '<div class="noimg">🎵</div>'}
          <div style="min-width:0"><div class="name" title="Показати треки">${esc(p.name)}</div><div class="r muted small">${p.count} трек${p.count % 10 === 1 && p.count % 100 !== 11 ? '' : (p.count % 10 >= 2 && p.count % 10 <= 4 && (p.count % 100 < 10 || p.count % 100 >= 20)) ? 'и' : 'ів'} · ${esc(p.createdBy)}</div></div>
          <div class="btns"><button class="primary go" title="Закинути весь плейлист у чергу впереміш">▶ у чергу</button>${(me.role === 'admin' || sameNick(p.createdBy, me.nick)) ? `<button class="ghost danger del" title="Видалити плейлист">✕</button>` : ''}</div>
        </div><div class="pl-tracks" data-for="${p.id}" hidden></div>`).join('') || '<div class="empty glek">Плейлистів ще нема. Перенеси готовий посиланням вище або створи порожній — треки додаються кнопкою «📂＋» під тим, що грає, в історії та в улюбленому.</div>');
    box.querySelector('#plImpForm').onsubmit = (e) => {
      e.preventDefault();
      if (!me.nick) { askNick(true); return; }
      openImport(e.target.querySelector('input').value, list);
    };
    drawImport(list);
    box.querySelector('#plCreate').onsubmit = async (e) => {
      e.preventDefault();
      const inp = e.target.querySelector('input');
      try { ok(await api('POST', '/api/playlists', { name: inp.value })); inp.value = ''; renderPlaylists(); } catch (err) { fail(err); }
    };
    box.querySelectorAll('.pl').forEach((el) => {
      const id = el.dataset.id;
      el.querySelector('.go').onclick = (e) => busy(e.currentTarget, 'закидаю…', () => api('POST', `/api/playlists/${id}/queue`, { shuffle: true }).then(ok).catch(fail));
      el.querySelector('.del')?.addEventListener('click', async () => {
        if (!confirm('Видалити плейлист?')) return;
        try { ok(await api('DELETE', `/api/playlists/${id}`)); renderPlaylists(); } catch (err) { fail(err); }
      });
      el.querySelector('.name').onclick = () => { if (openPl.has(id)) openPl.delete(id); else openPl.add(id); showPlTracks(id); };
      if (openPl.has(id)) showPlTracks(id);
    });
  }
  async function showPlTracks(id) {
    const box = document.querySelector(`.pl-tracks[data-for="${id}"]`);
    if (!box) return;
    if (!openPl.has(id)) { box.hidden = true; return; }
    box.hidden = false;
    box.innerHTML = '<div class="muted small"><span class="spin"></span> мить…</div>';
    try {
      const r = await api('GET', `/api/playlists/${id}`);
      box.innerHTML = `<ul class="list">${r.tracks.map((x) => `<li>
          ${cover(x.track)}
          <div style="min-width:0"><div class="t">${esc(x.track.title)} <span class="muted">· ${esc(x.track.artist)}</span></div><div class="r">${fmt(x.track.durationSec)} · від ${esc(fromNick(x.addedBy))}</div></div>
          <div class="btns"><button class="q" data-id="${esc(x.track.id)}">в чергу</button><button class="ghost danger rmt" data-id="${esc(x.track.id)}" title="Прибрати з плейлиста">✕</button></div>
        </li>`).join('') || '<li class="empty">порожньо</li>'}</ul>`;
      wireRows(box);
      box.querySelectorAll('.rmt').forEach((b) => b.onclick = async () => {
        try { ok(await api('DELETE', `/api/playlists/${id}/tracks/${b.dataset.id}`)); renderPlaylists(); } catch (err) { fail(err); }
      });
    } catch (e) { box.innerHTML = `<div class="empty">Ой-йой: ${esc(e.message)}</div>`; }
  }

  // ---------- playlist picker modal ----------
  let plTarget = null;
  async function openPlaylistPicker(trackId, title) {
    plTarget = trackId;
    $('plModal').hidden = false;
    $('plModal').querySelector('h3').textContent = `«${title}» — у який плейлист?`;
    const pick = $('plPick');
    pick.innerHTML = '<div class="muted small"><span class="spin"></span> мить…</div>';
    try {
      const list = await api('GET', '/api/playlists');
      pick.innerHTML = list.map((p) => `<button data-id="${p.id}"><span>${esc(p.name)}</span><span class="muted small">${p.count}</span></button>`).join('') || '<div class="muted small">Плейлистів ще нема, створи перший нижче.</div>';
      pick.querySelectorAll('button').forEach((b) => b.onclick = () => addToPlaylist(b.dataset.id));
    } catch (e) { pick.innerHTML = `<div class="empty">Ой-йой: ${esc(e.message)}</div>`; }
    setTimeout(() => $('plNewName').focus(), 50);
  }
  async function addToPlaylist(id) {
    try { ok(await api('POST', `/api/playlists/${id}/tracks`, { trackId: plTarget })); $('plModal').hidden = true; if (libTab === 'playlists') renderPlaylists(); }
    catch (e) { fail(e); }
  }
  $('plNewForm').onsubmit = async (e) => {
    e.preventDefault();
    const name = $('plNewName').value.trim();
    if (!name) return;
    try { const r = await api('POST', '/api/playlists', { name }); $('plNewName').value = ''; await addToPlaylist(r.id); }
    catch (err) { fail(err); }
  };
  $('plClose').onclick = () => { $('plModal').hidden = true; };
  $('plModal').addEventListener('click', (e) => { if (e.target === $('plModal')) $('plModal').hidden = true; });

  // ---------- «лише подивитись»: хто ще не назвався ----------
  // Раніше новенький бачив порожню сторінку під карткою «Хто прийшов?». Тепер ефір, черга й балачки видно одразу:
  // без хаба, звичайним GET /api/state раз на кілька секунд. Писати, закидати й грати — коли назвешся.
  let previewTimer = 0;
  let previewLast = 0;             // найбільший id рядка балачок, який уже намалювали
  async function previewTick() {
    if (conn) return;
    let r;
    try { r = await api('GET', '/api/state'); } catch { return; }
    if (conn || !r) return;
    state = r.state;
    render();
    const fresh = (r.chat || []).filter((m) => m.id > previewLast).sort((a, b) => a.id - b.id);
    fresh.forEach((m) => addMessage(m, !!previewLast, false));
    if (!previewLast) { $('messages').scrollTop = $('messages').scrollHeight; $('log').scrollTop = $('log').scrollHeight; }
    if (fresh.length) previewLast = fresh[fresh.length - 1].id;
  }
  function startPreview() {
    $('hello').hidden = false;
    document.body.classList.add('preview');
    paintNick();
    previewTick();
    clearInterval(previewTimer);
    previewTimer = setInterval(previewTick, 5000);
  }
  function stopPreview() {
    clearInterval(previewTimer);
    previewTimer = 0;
    $('hello').hidden = true;
    document.body.classList.remove('preview');
  }
  $('helloGo').onclick = () => askNick(true);

  // ---------- realtime ----------
  // Сервер перезапускається з кожним деплоєм, а телефон у кишені буває без мережі й довше. Тож пробуємо, поки не
  // вийде (типове — чотири спроби за 42 с), а не здаємось із «онови сторінку»: для того, хто слухає, F5 — це тиша.
  const RETRY_MS = [0, 1000, 2000, 3000, 5000];
  const retryIn = (r) => (r.elapsedMilliseconds > 120000 ? 10000 : RETRY_MS[Math.min(r.previousRetryCount, RETRY_MS.length - 1)]);
  function connect() {
    stopPreview();
    conn = new signalR.HubConnectionBuilder()
      .withUrl('/hub?nick=' + encodeURIComponent(me.nick))
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: retryIn })
      .build();
    conn.on('state', (s) => { state = s; render(); });
    conn.on('chat', (m) => {
      addMessage(m, true, true);
      // Мені щось подарували — оновити шафу: раптом це вміння (🎆 чи 💌), і кнопка має з'явитись одразу.
      if (m && m.kind === 'gift' && m.to && sameNick(m.to, me.nick) && me.account) HLavka.loadMine().then(lavkaChanged);
    });
    let tourRoom = null;
    if (window.HTournament) HTournament.connect((...a) => conn.invoke(...a));
    conn.on('tournament', (t) => {
      if (!window.HTournament) return;
      const hadCrown = JSON.stringify((HTournament.state || {}).crown || []);
      HTournament.update(t);
      if (hadCrown !== JSON.stringify((t && t.crown) || [])) { if (state) renderOnline(); repaintCrowns(); }
      // Нова гра турніру, і турнір посадив мене за неї — одразу за стіл (якщо вже в «Іграх»: на столі минулої гри,
      // у панелі турніру, у лобі), інакше — тост із підказкою. Саме «посадив», а не «я в списку»: хто вийшов із
      // турніру чи сидить за іншим столом, того сервер не садить, і висмикувати його нема куди.
      const room = t && t.stage === 'playing' && t.room ? t.room.id : null;
      const seated = !!room && (t.room.seats || []).some((p) => sameNick(p, me.nick));
      if (room && room !== tourRoom && seated) {
        if (tourRoom !== null || route === 'games') {
          if (route === 'games') go('#games/room/' + encodeURIComponent(room));
          else toast('🏆 Турнір: наступна гра почалась — гайда в «Ігри»!', 'ok');
        }
      }
      tourRoom = room || (tourRoom === null ? '' : tourRoom);
    });
    conn.on('chatDeleted', (ids) => removeMessages(ids || []));   // адмін прибрав репліки (web/moder.js)
    if (window.HModer) HModer.attach(conn);   // 📌, 🐢, 🔇, 🚫 — подія chatMod
    conn.on('chatLikes', (x) => {
      const el = x && $('messages').querySelector(`.msg[data-id="${x.id}"]`);
      if (el) paintLikes(el, x.likes || []);
    });
    conn.on('reaction', (r) => flyEmoji(r.emoji, r.nick));
    conn.on('fireworks', (x) => fireworks(x && x.nick));
    conn.on('look', (x) => HLavka.onLook(x));
    conn.on('padelLive', (x) => window.HGames && HGames.padel && HGames.padel(x));   // картка «🍳 Падельня» в лобі
    conn.on('fbUnread', fbOnUnread);   // «💡»: розробник відповів на мою записку
    conn.on('fbDev', fbOnDev);         // «💡» розробнику: нова записка чи відповідь людини
    HGames.attach(conn);           // усе про ігри — у web/games/core.js
    if (window.HVoice) HVoice.attach(conn);   // 🎙 Посиденьки — web/voice.js
    if (window.HBuy) HBuy.attach(conn);       // купити черепки — web/buy.js
    // Після HGames.attach: спершу хай каркас оновить свій список столів, а тоді вже перемальовуємо
    // кнопки в рядках. Історія балачок приходить раніше за перше лобі, тож без цього рядок про стіл
    // лишався б без кнопки аж до наступної новини з лобі.
    conn.on('rooms', () => paintRoomSlots());
    conn.on('chatHistory', (list) => {
      for (const id of ['messages', 'log']) { $(id).innerHTML = ''; delete $(id).dataset.done; }
      list.forEach((m) => addMessage(m, false));
      $('messages').scrollTop = $('messages').scrollHeight;
      $('log').scrollTop = $('log').scrollHeight;
    });
    conn.on('typing', typingSeen);
    // Балачка столу: уся розмова — щойно підписались на стіл (F5, реконект), далі — по рядку.
    conn.on('tableHistory', (x) => {
      if (!x || !x.id) return;
      // Зливаємо з тим, що вже є, за номером, а не замінюємо: рядок міг прилетіти подією раніше за історію.
      const byId = new Map((table.lines.get(x.id) || []).map((l) => [l.id, l]));
      for (const l of x.lines || []) byId.set(l.id, l);
      table.lines.set(x.id, [...byId.values()].sort((a, b) => a.id - b.id).slice(-100));
      if (x.id === table.id) renderTableLines();
    });
    conn.on('tableChat', (x) => {
      if (!x || !x.id || !x.line) return;
      const list = table.lines.get(x.id) || [];
      if (list.some((l) => l.id === x.line.id)) return;
      list.push(x.line);
      while (list.length > 100) list.shift();
      table.lines.set(x.id, list);
      if (x.id !== table.id) return;
      const l = x.line;
      const mine = sameNick(l.nick, me.nick);
      const box = tc.lines;
      const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
      appendTableLine(l, true);
      if (atBottom || mine) scrollTable();
      typingGone('table', l.nick);
      paintTableLast();
      const tagged = !mine && l.kind === 'chat' && taggedMe(l.text);
      if (tagged) ping();
      if (!mine && !tableVisible()) {
        table.unread++;
        paintTableBadge();
        if (tagged) toast(`@ ${l.nick} гукає тебе за столом: ${l.text}`.slice(0, 140));
      }
    });
    // Перезапуск сервера (деплой) рве зв'язок секунди на дві, і столи після нього ті самі (TablesKeeper) — такого люди
    // помічати не мають. Тож «зв'язок зник» кажемо, лише коли його нема довше LOST_QUIET_MS, а «знову на зв'язку» —
    // лише тим, кому перед тим сказали, що зник.
    const LOST_QUIET_MS = 4000;
    let lostTimer = 0;
    let lostSaid = false;
    const lost = (text) => {
      clearTimeout(lostTimer);
      lostTimer = setTimeout(() => { lostSaid = true; toast(text, 'wait'); }, LOST_QUIET_MS);
    };
    // Знову на зв'язку — після реконекту чи після того, як з'єднання довелось стартувати наново.
    const resync = () => {
      conn.invoke('SetNick', me.nick).catch(() => {});
      HLavka.loadLooks();            // поки зв'язку не було, хтось міг перевдягтись
      if (listening) conn.invoke('SetListening', true).catch(() => {});
      HGames.reconnected();
      if (window.HVoice) HVoice.reconnected();   // той самий позивний: з'єднання з людьми живуть і через деплой
      checkFront(true);              // зв'язок рветься здебільшого через деплой — глянути, що змінилось на сайті
      fbResync();                    // поки зв'язку не було, у записках могли відповісти
      clearTimeout(lostTimer);
      if (lostSaid) toast('Є! Знову на зв\'язку', 'ok');
      lostSaid = false;
    };
    conn.onreconnected(resync);
    conn.onreconnecting(() => lost('Ой-йой, зв\'язок зник — підключаюсь…'));
    // Автоповтор здається лише в рідкісних випадках (сервер закрив з'єднання назовсім) — тоді стартуємо його самі.
    const restart = () => conn.start().then(resync).catch(() => setTimeout(restart, 5000));
    conn.onclose(() => { lost('Ой-йой, зв\'язок урвався — підключаюсь наново…'); setTimeout(restart, 1000); });
    conn.start().then(() => {
      if (listening) conn.invoke('SetListening', true).catch(() => {});
      // Перші 'rooms' прилітають ще до того, як start() віддасть 'Connected', тож підписки на
      // кімнати треба попросити заново — як після реконекту.
      HGames.reconnected();
      if (route === 'lib') { libShown = libTab; loadLib(); }
    }).catch((e) => { toast('Халепа: не з\'єдналось — ' + e.message + '. Пробую ще раз', 'err'); setTimeout(connect, 4000); });
  }

  // ---------- оновлення без F5 ----------
  // web/ віддається наживо, тож після деплою ця вкладка живе зі старим кодом, а F5 рве музику. Сервер каже відбиток
  // кожного файлу (/api/front, Front.cs); перший — те, з чим вкладка стартувала. Змінений модуль гри каркас ігор
  // підхоплює сам (HGames.refresh), а змінилось інше, що ця сторінка вже вантажила, — плашка «Сайт оновився»:
  // сторінку людина оновить сама, коли їй зручно. Файли, яких сторінка ще не брала, нам байдужі — прийдуть свіжі.
  let front = null;              // файл → відбиток, з яким живе ця вкладка
  let frontAt = 0;
  function usedFiles() {
    const out = new Set(['index.html']);
    for (const el of document.querySelectorAll('script[src], link[rel="stylesheet"][href]')) {
      const u = new URL(el.src || el.href, location.href);
      if (u.origin === location.origin) out.add(u.pathname.slice(1));
    }
    return out;
  }
  // after — після реконекту: навіть коли файли ті самі, каталог ігор на новому сервері міг змінитись.
  // Звірки йдуть по черзі: реконект і повернення у вкладку разом підміняли б той самий модуль двічі.
  let frontQueue = Promise.resolve();
  const checkFront = (after) => (frontQueue = frontQueue.then(() => checkFrontNow(after)).catch((e) => console.warn('[front]', e)));
  async function checkFrontNow(after) {
    frontAt = Date.now();
    let files;
    try { files = (await api('GET', '/api/front')).files || {}; } catch { return; }
    if (!front) { front = files; return; }
    const changed = {};
    for (const f in files) if (front[f] !== files[f]) changed[f] = files[f];
    front = files;
    if (!after && !Object.keys(changed).length) return;
    const taken = new Set(HGames.refresh(changed));
    const used = usedFiles();
    if (Object.keys(changed).some((f) => !taken.has(f) && used.has(f))) $('updBar').hidden = false;
  }
  $('updLater').onclick = () => { $('updBar').hidden = true; };
  $('updGo').onclick = () => {
    // Грала музика — після перезавантаження спробуємо врубити її самі (resumeAudio нижче).
    try { if (playState !== 'idle') sessionStorage.setItem('resumeAudio', '1'); } catch { /* приватне вікно */ }
    location.reload();
  };
  // Фронт буває змінено й без перезапуску сервера — тоді звіряємось, коли людина вертається у вкладку, і зрідка просто так.
  document.addEventListener('visibilitychange', () => { if (!document.hidden && Date.now() - frontAt > 60000) checkFront(false); });
  setInterval(() => { if (!document.hidden) checkFront(false); }, 10 * 60 * 1000);

  // Після «Оновити» на плашці: чекаємо адресу потоку (вона в state) і врубаємо. Chrome дозволяє — жест був на цьому
  // ж сайті; інший браузер може й не дати, тоді startAudio просто підкаже натиснути кнопку.
  function resumeAudio() {
    let want = false;
    try { want = sessionStorage.getItem('resumeAudio') === '1'; sessionStorage.removeItem('resumeAudio'); } catch { /* приватне вікно */ }
    if (!want) return;
    const until = Date.now() + 20000;
    const tick = () => {
      if (playState !== 'idle') return;
      if (state?.streamUrl) startAudio(true);
      else if (Date.now() < until) setTimeout(tick, 300);
    };
    tick();
  }

  // ---------- нічний відбій ----------
  // Кого він стосується, каже /api/me (night: { from, to, text }), решті там null. Котра година в Києві — рахуємо
  // тут і щопівхвилини: плашка має з'явитись опівночі й зникнути о шостій без перезавантаження сторінки.
  // Не пускає за стіл однаково сервер; плашка лише каже, чому.
  function kyivHour() {
    for (const timeZone of ['Europe/Kyiv', 'Europe/Kiev']) {
      try { return Number(new Intl.DateTimeFormat('en-GB', { timeZone, hour: 'numeric', hourCycle: 'h23' }).format(new Date())); }
      catch { /* стара база зон — пробуємо стару назву */ }
    }
    return new Date().getHours();
  }
  function paintNight() {
    const n = me.night;
    const h = kyivHour();
    const on = !!n && (n.from <= n.to ? h >= n.from && h < n.to : h >= n.from || h < n.to);
    if (on) $('nightBar').querySelector('.nb-text').textContent = n.text;
    $('nightBar').hidden = !on;
  }
  setInterval(paintNight, 30000);

  // ---------- boot ----------
  setPlayUi();
  // Люди й цифри (web/people.js): профіль, картка по кліку на нік, «📊 Хто скільки». Їм потрібні ті самі рядки
  // треків і ті самі дні/час, що й Бібліотеці, — віддаємо свої, щоб не завести других.
  HPeople.init({
    $, esc, api, toast, busy, me, go, askNick, fmt, tm, dayTime, dayLabel, plural, cover, trackRow, wireRows, nickHtml, isMobile,
    dj, sameNick, state: () => state,
    ping: () => ping(),
    // 🎺 гімн у профілі: прослухати тим самим плеєром, що й за столом
    toggleAnthem, stopAnthem, anthemPlaying, paintAnthemBtns,
    mention: (nick) => { const inp = $('chatInput'); inp.value = (inp.value ? inp.value.replace(/\s*$/, ' ') : '') + '@' + nick + ' '; if (isMobile()) go('#chat'); else setChatOpen(true); setChatTab('chat'); inp.focus(); },
  });
  // Лавка Дядька Глека (web/lavka.js): вітрина, а ще — хто як вбраний. Своє купив чи вдягнув — перемалювати шапку,
  // ефір (🎆) і чергу (💌): там кнопки вмінь.
  const lavkaChanged = () => { paintNick(); nowSig = ''; queueSig = ''; if (state) render(); };
  HLavka.init({ $, esc, api, toast, busy, me, go, askNick, onMine: lavkaChanged, onLooks: () => { if (state) renderOnline(); },
    playAnthem, toggleAnthem, stopAnthem, anthemPlaying, paintAnthemBtns, playFx,
    // 😈 на кого наслати прокльон: хто зараз на сайті (гостей Лавка відсіє сама)
    online: () => (state && state.online) || [] });
  HLavka.loadLooks();
  // 🏺 Купити черепки за гривні (web/buy.js): вікно з пакетами; продавцю — «чекають підтвердження»
  if (window.HBuy) HBuy.init({ esc, api, toast, busy, me, askNick, dayTime, online: () => (state && state.online) || [] });
  // 🔥 Жива реклама: картка прожарки в Лавці й блок у вкладці «📣 Реклама» (web/liveads.js)
  if (window.HLiveAds) HLiveAds.init({ esc, api, toast, busy, me, askNick, onBalance: () => HLavka.refresh() });
  // 🛡 Модерація Балачок (web/moder.js): 📌 плашка, поле вводу під 🔇, кнопки адміна, вкладка в Бібліотеці
  if (window.HModer) HModer.init({ esc, toast, busy, me, dayTime, conn: () => conn, tableInput: () => tc.input });
  // 🎙 Посиденьки (web/voice.js): duck — притишити радіо (1 — як на повзунку), onRoster — хто в голосі змінився.
  if (window.HVoice) HVoice.init({
    $, esc, toast, me, askNick,
    duck: (f) => { duckBy = f; audio.volume = posToVol(+vol.value) * duckBy * duckAnthem; },
    onRoster: () => { if (state) renderOnline(); },
  });
  // onTable — біля якого столу ми стоїмо (балачка столу), openTable — кнопка «До суперечки» в картці гри,
  // onTurn — за якими столами мій хід (заголовок вкладки й «Ігри»), online — хто на сайті (кого покликати за стіл).
  HGames.init({
    $, esc, toast, busy, api, me, root: $('games'), go, onTable, openTable: () => openTable(true),
    onTurn, ping: () => ping(), online: () => (state && state.online) || [], askNick: () => askNick(true),
    // 🎺 гімн переможця: стіл каже, що грати, а грає й притишує радіо app.js
    anthem: (a, opt) => playAnthem(a, opt), stopAnthem,
    // 🎉 святкування переможця — шар поверх картки столу (flair.md §3)
    fx: (host, id, opt) => playFx(host, id, opt),
  });
  setLogFilter(logFilter);
  applyRoute();
  checkFront(false);   // перший відбиток — те, з чим стартувала вкладка
  resumeAudio();
  // Хто я — каже сервер: акаунт із куки або гість із приставкою до того, що лежить у localStorage.
  // Тому підключаємось до хабу лише після /api/me: інакше me.nick розійшовся б із тим, як нас звуть за столами.
  api('GET', '/api/me').then((m) => {
    me.role = m.role;
    me.account = !!m.account;
    me.hasPassword = !!m.hasPassword;
    me.google = !!m.google;
    me.email = m.email || '';
    me.banPrice = m.banPrice || 0;
    me.shards = m.shards || null;   // { buy, sell } — черепки за гривні увімкнено в конфігу (web/buy.js)
    me.night = m.night || null;
    paintNight();
    loadGoogle(m.googleClientId);
    $('adsTab').hidden = me.role !== 'admin';
    $('fbTab').hidden = me.role !== 'admin';
    $('photosTab').hidden = me.role !== 'admin';
    $('modTab').hidden = me.role !== 'admin';
    // на #lib/ads, записки, фото чи модерацію зайшов не адмін — відкриваємо звичайну вкладку, а не порожню сторінку
    if ((libTab === 'ads' || libTab === 'feedback' || libTab === 'photos' || libTab === 'mod') && me.role !== 'admin') go('#lib/history');
    if (me.account || me.nick) {
      // Нік без приставки з часів до акаунтів: сервер уже зве нас «гість …» — запропонуємо закріпити його паролем.
      const plain = !me.account && me.nick && m.nick !== me.nick ? me.nick : null;
      me.nick = m.nick;
      localStorage.setItem('nick', m.nick);
      paintNick();
      connect();
      if (me.account) HLavka.loadMine().then(lavkaChanged);
      if (window.HBuy) HBuy.ready();
      if (plain) askNick(true, 'register', plain);
      // «💡»: адміну — скільки записок чекає, решті — у скількох своїх записках нова відповідь розробника.
      if (me.role === 'admin') loadFeedbackCount(); else loadMyFeedback(false);
    } else startPreview();
    if (state) render();
  }).catch(() => {
    if (me.nick) { paintNick(); connect(); } else startPreview();
  });
})();
