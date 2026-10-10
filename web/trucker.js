// 🚚 Для далекобійників — картка у своєму профілі («Я»), а не в шапці: треба вона двом-трьом людям, не всім.
//
// 1. Радіо в ETS2/ATS: бат, що дописує станцію в live_streams.sii гри. Збираємо його тут же в браузері — сервер .bat
//    не віддає. Потік http://…:8000/radio.mp3 Caddy віддає суцільно (header_down Transfer-Encoding identity), тож
//    ретранслятор і вікно, яке треба тримати відкритим, більше не потрібні.
//
// 2. Скіп кнопкою керма. Граєш, Глечики відкриті в браузері — кнопка на кермі (чи будь-якому паді) перемикає трек, як ⏭.
//    Яка кнопка — кожен призначає сам у себе, лежить у localStorage цього браузера.
//    Кермо читаємо напряму, не через HPad: pad.js кермо навмисно не бачить (передачі G29 — це кнопки, вони водили сайт),
//    а без фокуса вікна не тисне нічого. Тут навпаки: потрібна саме одна кнопка керма і саме тоді, коли фокус у грі.
//    Опитуємо самі (подій на кнопки пада браузер не шле), такт — від Worker'а: вкладку, яку не видно, Chrome гальмує
//    до разу на секунду, а за п'ять хвилин — до разу на хвилину, і коротке натискання губилось би. Але свіжі дані пада
//    Chrome дає лише сторінці, яку видно (вікно не згорнуте й не закрите грою на весь екран) — про це підказка.
(() => {
  const KEY = 'wheelSkip';       // { id: назва пристрою від браузера, idx: номер кнопки, name: коротка назва }
  const AT_KEY = 'wheelSkipAt';  // коли востаннє скіпали кермом — спільне для всіх вкладок цього браузера
  const GAP_MS = 4000;           // друге натискання одразу — не другий скіп: трек ще не встиг змінитись
  const TICK_MS = 30;
  const STREAM = 'http://hlechyky.pp.ua:8000/radio.mp3';
  const OLD_RELAY = 'http://127.0.0.1:8765/radio.mp3';   // ретранслятор із бата, яким ділились до 10.10

  let o = null;                  // { esc, toast, skip }
  let bind = load();
  let binding = false;
  let box = null;                // .tr-wheel у картці профілю, поки вона на екрані
  let ticker = null, fallbackTimer = 0;
  const prev = new Map();        // index пада → які кнопки були натиснуті минулого такту
  let seen = '';                 // які пади видно — щоб перемальовувати картку лише на зміну

  function load() {
    try {
      const v = JSON.parse(localStorage.getItem(KEY) || 'null');
      return v && typeof v.id === 'string' && Number.isInteger(v.idx) ? v : null;
    } catch { return null; }
  }
  function save() {
    try { bind ? localStorage.setItem(KEY, JSON.stringify(bind)) : localStorage.removeItem(KEY); } catch { /* приватне вікно */ }
  }

  const shown = () => !!(box && box.isConnected);
  const pads = () => [...((navigator.getGamepads && navigator.getGamepads()) || [])].filter(Boolean);
  /// «G29 Driving Force Racing Wheel (Vendor: 046d Product: c24f)» → «G29 Driving Force Racing Wheel».
  const shortName = (id) => String(id || '').replace(/\s*\((?:STANDARD GAMEPAD\s*)?Vendor:.*\)\s*$/i, '').replace(/^[0-9a-f]{4}-[0-9a-f]{4}-/i, '').trim() || 'пад';
  const label = (b) => `${b.name} · кнопка ${b.idx + 1}`;

  // ---------- такт і натискання ----------
  function tick() {
    // пішли з профілю, поки чекали кнопку, — не призначати першу-ліпшу, натиснуту вже в грі
    if (binding && !shown()) { binding = false; sync(); return; }
    const list = pads();
    const sig = list.map((g) => g.index + ':' + g.id).join('|');
    if (sig !== seen) { seen = sig; paint(); }
    for (const g of list) {
      const now = g.buttons.map((b) => b.pressed);
      const was = prev.get(g.index);
      prev.set(g.index, now);
      if (!was) continue;          // пад щойно з'явився: що вже затиснуте — не натискання
      for (let i = 0; i < now.length; i++) if (now[i] && !was[i]) press(g, i);
    }
  }

  function press(g, i) {
    if (binding) {
      bind = { id: g.id, idx: i, name: shortName(g.id) };
      save();
      binding = false;
      sync();
      paint();
      o.toast(`Готово: ${label(bind)} — скіп`, 'ok');
      return;
    }
    if (bind && g.id === bind.id && i === bind.idx) fire();
  }

  /// Дві відкриті вкладки бачать те саме натискання — скіпає лише одна (замок браузера), і не частіше за GAP_MS.
  function fire() {
    const go = () => {
      let at = 0;
      try { at = +localStorage.getItem(AT_KEY) || 0; } catch { /* нема — то й нема */ }
      if (Date.now() - at < GAP_MS) return;
      try { localStorage.setItem(AT_KEY, String(Date.now())); } catch { /* приватне вікно */ }
      o.skip();
    };
    if (navigator.locks) navigator.locks.request('hl-wheel-skip', { ifAvailable: true }, (lock) => { if (lock) go(); });
    else go();
  }

  /// Опитуємо лише тоді, коли є що ловити: кнопку призначено або чекаємо, яку натиснуть.
  function sync() {
    const need = !!navigator.getGamepads && (binding || !!bind);
    if (need && !ticker && !fallbackTimer) {
      try {
        const src = `setInterval(() => postMessage(0), ${TICK_MS});`;
        ticker = new Worker(URL.createObjectURL(new Blob([src], { type: 'text/javascript' })));
        ticker.onmessage = tick;
      } catch {
        ticker = null;
        fallbackTimer = setInterval(tick, TICK_MS);   // без Worker'а — хоча б так, у видимій вкладці працює
      }
    }
    if (!need) {
      if (ticker) { ticker.terminate(); ticker = null; }
      if (fallbackTimer) { clearInterval(fallbackTimer); fallbackTimer = 0; }
      prev.clear();
    }
  }

  // ---------- бат для гри ----------
  /// Файл водночас .bat і PowerShell: cmd бачить «<# :» як мітку і запускає PowerShell на самого себе, PowerShell
  /// бачить усе до «#>» як коментар. Лише дописує станцію, а рядок старого ретранслятора міняє на прямий потік.
  /// PowerShell-частина навмисно без бектіків (екранування PowerShell) — інакше вона б'ється з шаблоном JS.
  function batText() {
    const head = [
      '<# : Hlechyky radio for ETS2 / ATS (this file is both a .bat and a PowerShell script)',
      '@echo off',
      'title Hlechyky radio for ATS / ETS2',
      'set "HLR_SELF=%~f0"',
      'powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ([IO.File]::ReadAllText($env:HLR_SELF, [Text.Encoding]::UTF8))"',
      'pause',
      'exit /b',
      '#>',
    ].join('\n');
    const ps = String.raw`
$Url   = '${STREAM}'
$Old   = '${OLD_RELAY}'
$Entry = $Url + '|Hlechyky|Various|UA|192|0'
$Q     = [char]34
$NL    = [Environment]::NewLine
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$utf8 = New-Object Text.UTF8Encoding $false

function Add-Station([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        $text = 'SiiNunit' + $NL + '{' + $NL + 'live_stream_def : .live_streams {' + $NL + ' stream_data[]: ' + $Q + $Entry + $Q + $NL + '}' + $NL + '}' + $NL
        [IO.File]::WriteAllText($path, $text, $utf8)
        return 'created'
    }
    $raw = [IO.File]::ReadAllText($path)
    if ($raw.Contains($Url)) { return 'present' }
    Copy-Item -LiteralPath $path -Destination ($path + '.bak') -Force
    if ($raw.Contains($Old)) {
        [IO.File]::WriteAllText($path, $raw.Replace($Old, $Url), $utf8)
        return 'updated'
    }
    $lines = New-Object Collections.Generic.List[string]
    $lines.AddRange([IO.File]::ReadAllLines($path))
    $countIdx = -1; $lastItemIdx = -1; $defIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*stream_data\s*:\s*\d+\s*$') { $countIdx = $i }
        elseif ($lines[$i] -match '^\s*stream_data\[\d*\]\s*:') { $lastItemIdx = $i }
        elseif ($defIdx -lt 0 -and $lines[$i] -match '^\s*live_stream_def\s*:.*\{') { $defIdx = $i }
    }
    if ($countIdx -ge 0) {
        # як пише сама гра: «stream_data: N», далі пронумеровані рядки
        $n = [int]($lines[$countIdx] -replace '\D', '')
        $lines[$countIdx] = ' stream_data: ' + ($n + 1)
        $lines.Insert([Math]::Max($countIdx, $lastItemIdx) + 1, ' stream_data[' + $n + ']: ' + $Q + $Entry + $Q)
    } elseif ($defIdx -ge 0) {
        $lines.Insert($defIdx + 1, ' stream_data[]: ' + $Q + $Entry + $Q)
    } else { return 'unknown' }
    [IO.File]::WriteAllLines($path, $lines, $utf8)
    return 'added'
}

$docs = [Environment]::GetFolderPath('MyDocuments')
$games = @(
    @{ Title = 'American Truck Simulator'; Dir = Join-Path $docs 'American Truck Simulator'; Proc = 'amtrucks' },
    @{ Title = 'Euro Truck Simulator 2';   Dir = Join-Path $docs 'Euro Truck Simulator 2';   Proc = 'eurotrucks2' }
) | Where-Object { Test-Path -LiteralPath $_.Dir }
if (-not $games) {
    Write-Host ('Не знайшов папки гри в ' + $docs + '. Запусти гру хоча б раз і спробуй знову.') -ForegroundColor Red
    return
}
foreach ($g in $games) {
    $file = Join-Path $g.Dir 'live_streams.sii'
    if ((Test-Path -LiteralPath $file) -and ([IO.File]::ReadAllText($file)).Contains($Url)) {
        Write-Host ($g.Title + ': Глечики вже є.') -ForegroundColor Green
        continue
    }
    while (Get-Process -Name $g.Proc -ErrorAction SilentlyContinue) {
        Write-Host ($g.Title + ' зараз запущена: при виході вона перепише файл. Закрий гру й натисни Enter.') -ForegroundColor Yellow
        [void](Read-Host)
    }
    switch (Add-Station $file) {
        'present' { Write-Host ($g.Title + ': Глечики вже є.') -ForegroundColor Green }
        'added'   { Write-Host ($g.Title + ': Глечики додано (копія старого файла: live_streams.sii.bak).') -ForegroundColor Green }
        'updated' { Write-Host ($g.Title + ': Глечики переведено на прямий потік, старий ретранслятор більше не треба.') -ForegroundColor Green }
        'created' { Write-Host ($g.Title + ': Глечики додано.') -ForegroundColor Green }
        'unknown' { Write-Host ($g.Title + ': не зміг розібрати live_streams.sii, додай рядок руками: ' + $Url) -ForegroundColor Red }
    }
}
Write-Host ''
Write-Host 'У грі: Радіо -> Онлайн-радіо -> Hlechyky. Це вікно можна закрити.' -ForegroundColor Cyan
`;
    return (head + ps).replace(/\r?\n/g, '\r\n');   // cmd із самими LF плутається в рядках біля міток
  }

  function downloadBat() {
    const url = URL.createObjectURL(new Blob([batText()], { type: 'application/octet-stream' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = 'Hlechyky-radio.bat';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  }

  // ---------- картка в профілі ----------
  function cardHtml() {
    const e = o.esc;
    return '<section class="panel wcard trucker"><h3>🚚 Для далекобійників <span class="muted small">ETS2 / ATS</span></h3>'
      + '<h4>📻 Глечики в радіо гри</h4>'
      + '<div class="tr-row"><button type="button" class="primary" data-tr="bat">⬇ Бат для гри</button>'
      + '<span class="muted small">Запусти один раз — у грі з\'явиться станція <b>Hlechyky</b> (Радіо → Онлайн-радіо). '
      + 'Старий ретранслятор теж переведе на прямий потік.</span></div>'
      + '<details class="tr-hand"><summary class="muted small">Або руками</summary><div class="muted small">'
      + 'Закрий гру, відкрий <code>Документи\\American Truck Simulator\\live_streams.sii</code> (чи Euro Truck Simulator 2) '
      + 'і додай у список <code>stream_data</code> рядок, поправивши номер N і кількість над списком:'
      + '<pre>stream_data[N]: "' + e(STREAM) + '|Hlechyky|Various|UA|192|0"</pre></div></details>'
      + '<h4>🛞 Скіп кнопкою керма</h4><div class="tr-wheel"></div></section>';
  }

  function paint() {
    if (!shown()) return;
    const e = o.esc;
    if (!navigator.getGamepads) { box.innerHTML = '<div class="muted small">Цей браузер не бачить керма й падів.</div>'; return; }
    const list = pads();
    const mine = bind && list.some((g) => g.id === bind.id);
    const state = binding
      ? '<div class="tr-wait"><span class="spin"></span> Натисни на кермі кнопку, яка буде скіпом… <small class="muted">(Esc — відміна)</small></div>'
      : bind
        ? `<div>Скіп: <b>${e(label(bind))}</b></div>`
        : '<div>Кнопку ще не призначено</div>';
    const seenTxt = list.length
      ? (bind && !mine
        ? '<span class="tr-bad">Призначеного керма зараз не видно — натисни на ньому будь-яку кнопку</span>'
        : '<span class="tr-ok">Бачу: ' + list.map((g) => e(shortName(g.id))).join(', ') + '</span>')
      : '<span class="tr-bad">Браузер ще не бачить керма — натисни на ньому будь-яку кнопку, поки ця вкладка перед очима</span>';
    box.innerHTML = state
      + '<div class="small">' + seenTxt + '</div>'
      + '<div class="tr-row">'
      + `<button type="button" data-tr="bind" class="${bind ? '' : 'primary'}">${binding ? 'Відміна' : bind ? 'Інша кнопка' : 'Призначити'}</button>`
      + (bind && !binding ? '<button type="button" data-tr="off" class="ghost">Прибрати</button>' : '')
      + '</div>'
      + '<div class="muted small">Глечики відкриті в браузері, ти в грі: кнопка перемикає трек для всіх, як ⏭. '
      + 'Браузер чує кермо, лише коли вікно з сайтом <b>видно</b> — не згорнуте й не сховане за грою на весь екран. '
      + 'Найпростіше — тримати його на другому моніторі або грати у вікні.</div>';
    box.querySelector('[data-tr="bind"]').onclick = () => { binding = !binding; sync(); paint(); };
    const off = box.querySelector('[data-tr="off"]');
    if (off) off.onclick = () => { bind = null; save(); sync(); paint(); };
  }

  /// Після того як профіль намалював cardHtml(): кнопки картки й живий стан керма.
  function wire(root) {
    const card = root.querySelector('.trucker');
    if (!card) return;
    card.querySelector('[data-tr="bat"]').onclick = downloadBat;
    box = card.querySelector('.tr-wheel');
    paint();
  }

  window.HTrucker = {
    init(opts) {
      o = opts;
      addEventListener('keydown', (ev) => {
        if (ev.code === 'Escape' && binding) { binding = false; sync(); paint(); }
      });
      // Пад з'явився чи зник — показати це в картці відразу, навіть якщо кнопку ще не призначено (тоді такту нема).
      addEventListener('gamepadconnected', paint);
      addEventListener('gamepaddisconnected', (ev) => { prev.delete(ev.gamepad.index); paint(); });
      sync();
    },
    cardHtml: () => (o ? cardHtml() : ''),
    wire,
    batText,
  };
})();
