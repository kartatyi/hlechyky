<#
.SYNOPSIS
  Глечики — launcher. build | start | stop | restart | back | radio | status | logs | autostart | watchdog

  start     — liquidsoap + сервер + Caddy у фоні (логи в logs\server.log, logs\liquidsoap.log, logs\caddy.log); знімає автонагляд з паузи
  build     — зібрати Release у build\ (start робить це сам, якщо build\ порожній)
  stop      — зупинити Caddy, сервер і liquidsoap; автонагляд стає на паузу, доки не буде start
  restart   — перезапустити сервер на свіжій збірці так, щоб люди майже не помітили: збірка — поруч у build.next, поки
              старий сервер працює (deploy.ps1 кладе її туди заздалегідь); столи — у знімок (/api/internal/freeze), і
              новий сервер підніме їх із тими самими id; сам простій — лише підміна теки й старт (~2 с), а Caddy цей
              час притримує запити. Caddy і liquidsoap не чіпає (слухачі не відвалюються); змінився Caddyfile — reload
  back      — відкат: попередня збірка (build.prev) назад у build\ і перезапуск (deploy.ps1, коли новий сервер не піднявся)
  radio     — перезапустити liquidsoap (після правок liquidsoap\radio.liq); ефір замовкне на кілька секунд.
              -Why "<чому>" — це не рука, а збій (так кличе сервер, коли годинник ефіру став): рядок у logs\watchdog.log
              і знімок завислого логу logs\liquidsoap.frozen-*.log
  status    — що працює
  logs      — хвіст логу сервера
  autostart — завдання «Hlechyky» у Планувальнику: при вході у Windows і щохвилини запускає watchdog
  watchdog  — одна перевірка: піднімає те, що впало (сервер, Caddy, liquidsoap), завислий сервер
              перезапускає. Пише в logs\watchdog.log лише тоді, коли щось робить

  liquidsoap живе в tools\liquidsoap (звичайна Windows-збірка, качає setup.ps1) і сам віддає потік на 127.0.0.1:8001/radio.mp3 —
  ні Docker, ні Icecast більше не потрібні. Без tools\caddy\caddy.exe крок Caddy пропускається. Дивись CONTRIBUTING.md.
#>
param([ValidateSet('build', 'start', 'stop', 'restart', 'back', 'radio', 'status', 'logs', 'autostart', 'watchdog')][string]$Cmd = 'status',
    [string]$Why = '')

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Build = Join-Path $Root 'build'
$Dll = Join-Path $Build 'Hlechyky.dll'
# Збірка «на підміну»: publish іде сюди, поки старий сервер працює з build\ (він тримає там файли), а перезапуск лише
# міняє теки місцями. build.prev — попередня збірка для відкату (back).
$BuildNext = Join-Path $Root 'build.next'
$BuildPrev = Join-Path $Root 'build.prev'
$BuiltSha = Join-Path $Root 'data\built.sha'
$NextSha = Join-Path $Root 'data\next.sha'
$PrevSha = Join-Path $Root 'data\prev.sha'
$ControlKey = Join-Path $Root 'data\control.key'
$Tables = Join-Path $Root 'data\tables.json'
# Копія чистого знімка, знятого перед перезапуском: якщо новий сервер не підніметься, back поверне столи з неї.
$TablesClean = Join-Path $Root 'data\tables.clean.json'
$CaddyApplied = Join-Path $Root 'data\caddyfile.sha'
$Log = Join-Path $Root 'logs\server.log'
$ErrLog = Join-Path $Root 'logs\server.err.log'
$PidFile = Join-Path $Root 'data\server.pid'
$Caddy = Join-Path $Root 'tools\caddy\caddy.exe'
$Caddyfile = Join-Path $Root 'Caddyfile'
$CaddyPidFile = Join-Path $Root 'data\caddy.pid'
$StopFlag = Join-Path $Root 'data\stopped.flag'
$WatchState = Join-Path $Root 'data\watchdog.json'
$WatchLog = Join-Path $Root 'logs\watchdog.log'
$DeployLock = Join-Path $Root 'data\deploy.lock'
$TaskName = 'Hlechyky'
$Vbs = Join-Path $Root 'Hlechyky.vbs'
$env:HLECHYKY_ROOT = $Root   # читають і сервер, і Caddyfile ({$HLECHYKY_ROOT})
$Liq = Join-Path $Root 'tools\liquidsoap\liquidsoap.exe'
$LiqScript = Join-Path $Root 'liquidsoap\radio.liq'
$LiqEnvFile = Join-Path $Root 'liquidsoap\.env'
$LiqPidFile = Join-Path $Root 'data\liquidsoap.pid'
$LiqLog = Join-Path $Root 'logs\liquidsoap.log'
$LiqErrLog = Join-Path $Root 'logs\liquidsoap.err.log'
$SpareList = Join-Path $Root 'data\spare.m3u'
# Windows-збірка liquidsoap тече пам'яттю на кожного слухача (див. radio.liq, chunk). Понад Quiet МБ — перезапуск, щойно
# ніхто не слухає; понад Hard — перезапуск будь-що (слухачі перепідключаться). Міряємо виділене (private bytes), не
# робочий набір: витеклого ніхто не торкається, Windows виносить його в pagefile, і 9.10.2026 процес тримав 5,9 ГБ при
# робочому наборі 85 МБ — наглядач цього просто не бачив.
$LiqMemQuietMB = 800
$LiqMemHardMB = 2500

function Get-ProcessFromPidFile([string]$File, [string]$Name) {
    if (-not (Test-Path $File)) { return $null }
    $p = Get-Process -Id (Get-Content $File) -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq $Name) { return $p }
    Remove-Item $File -ErrorAction SilentlyContinue
    return $null
}

function Get-Server { Get-ProcessFromPidFile $PidFile 'dotnet' }
function Get-Caddy { Get-ProcessFromPidFile $CaddyPidFile 'caddy' }
function Get-Liquidsoap { Get-ProcessFromPidFile $LiqPidFile 'liquidsoap' }

# Процес, що слухає порт, якщо це саме той, кого чекаємо (pid-файл загубився, а процес живий).
function Find-Listener([int]$Port, [string]$Name) {
    $c = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $c) { return $null }
    $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq $Name) { return $p }
    return $null
}

function Invoke-Build {
    Write-Host 'Збираю Release…'
    dotnet publish (Join-Path $Root 'src\Hlechyky\Hlechyky.csproj') -c Release -o $Build --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish впав' }
    # Позначка для deploy.ps1: з якого коміту зібрано те, що зараз лежить у build\
    New-Item -ItemType Directory -Force (Join-Path $Root 'data') | Out-Null
    try { Set-Content $BuiltSha (git -C $Root rev-parse HEAD) -Encoding ASCII } catch { }
}

# Збірка поруч, поки сервер працює: у build.next із робочої копії. deploy.ps1 збирає туди сам (з окремої копії коду,
# щоб web\ на проді не змінився раніше за сервер) — тоді data\next.sha уже дорівнює HEAD, і тут нічого не робиться.
function Invoke-BuildNext {
    $head = (git -C $Root rev-parse HEAD).Trim()
    if ((Test-Path (Join-Path $BuildNext 'Hlechyky.dll')) -and (Test-Path $NextSha) -and (Get-Content $NextSha -Raw).Trim() -eq $head) {
        Write-Host "Збірка $($head.Substring(0, 7)) уже лежить готова (build.next)"
        return
    }
    Write-Host 'Збираю Release поруч (build.next), сервер поки працює…'
    if (Test-Path $BuildNext) { Remove-Item $BuildNext -Recurse -Force }
    dotnet publish (Join-Path $Root 'src\Hlechyky\Hlechyky.csproj') -c Release -o $BuildNext --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish впав' }
    New-Item -ItemType Directory -Force (Join-Path $Root 'data') | Out-Null
    Set-Content $NextSha $head -Encoding ASCII
}

# build.next → build\, а те, що було, — у build.prev (для back). Сервер у цю мить зупинений. Файли щойно вбитого процесу
# Windows відпускає не миттєво, тож кілька спроб; не вийшло — $false, і restart збере просто в build\, як раніше.
function Switch-Build {
    if (-not (Test-Path (Join-Path $BuildNext 'Hlechyky.dll'))) { return $false }
    for ($i = 0; $i -lt 20; $i++) {
        try {
            if (Test-Path $BuildPrev) { Remove-Item $BuildPrev -Recurse -Force -ErrorAction Stop }
            if (Test-Path $Build) { Move-Item $Build $BuildPrev -ErrorAction Stop }
            Move-Item $BuildNext $Build -ErrorAction Stop
            if (Test-Path $BuiltSha) { Copy-Item $BuiltSha $PrevSha -Force }
            Move-Item $NextSha $BuiltSha -Force
            Write-Host "Збірку підмінено: build\ = $((Get-Content $BuiltSha -Raw).Trim().Substring(0, 7))"
            return $true
        }
        catch {
            # build\ уже переїхав у build.prev, а build.next не встиг стати на його місце — повертаємо як було
            if (-not (Test-Path $Build) -and (Test-Path $BuildPrev) -and (Test-Path $BuildNext)) { try { Move-Item $BuildPrev $Build } catch { } }
            Start-Sleep -Milliseconds 150
        }
    }
    Write-Host 'Увага: build.next не став на місце build\ — збираю просто в build\'
    return $false
}

# back: попередня збірка назад. Сервер має бути зупинений. Невдалу збірку не стираємо, а відкладаємо в build.bad: файли
# щойно вбитого процесу Windows відпускає не миттєво, і Remove-Item посеред теки лишив би ні ту, ні ту.
function Restore-PrevBuild {
    if (-not (Test-Path (Join-Path $BuildPrev 'Hlechyky.dll'))) { throw 'Нема build.prev — відкочуватись нема на що' }
    $bad = Join-Path $Root 'build.bad'
    for ($i = 0; $i -lt 20; $i++) {
        try {
            if (Test-Path $bad) { Remove-Item $bad -Recurse -Force -ErrorAction Stop }
            if (Test-Path $Build) { Move-Item $Build $bad -ErrorAction Stop }
            Move-Item $BuildPrev $Build -ErrorAction Stop
            if (Test-Path $PrevSha) { Move-Item $PrevSha $BuiltSha -Force }
            Write-Host 'Повернув попередню збірку (build.prev → build\, невдала — у build.bad)'
            return
        }
        catch { Start-Sleep -Milliseconds 150 }
    }
    throw 'build.prev не став на місце build\ (файли зайняті)'
}

function Get-ListenPort {
    foreach ($name in 'appsettings.Local.json', 'appsettings.json') {
        $file = Join-Path $Root $name
        if (-not (Test-Path $file)) { continue }
        try {
            $port = (Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json).Site.ListenPort
            if ($port) { return [int]$port }
        } catch { }
    }
    return 8080
}

# Столи — у знімок перед тим, як сервер уб'ють (Games\TablesKeeper.cs): ходи завмирають, новий сервер підніме столи з
# тими самими id. Старий сервер без цього вміння (чи без ключа) — перезапуск, як раніше: столи зникнуть.
function Invoke-Freeze([int]$TimeoutSec = 10) {
    if (-not (Get-Server)) { return $false }
    if (-not (Test-Path $ControlKey)) { Write-Host 'Столи: нема data\control.key (сервер ще без знімків) — перезапуск без них'; return $false }
    try {
        $key = (Get-Content $ControlKey -Raw).Trim()
        $r = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$(Get-ListenPort)/api/internal/freeze" -Headers @{ 'X-Control-Key' = $key } -TimeoutSec $TimeoutSec
        Write-Host "Столи заморожено: у знімку $($r.tables), партій грає далі $($r.resumes), переривається $($r.interrupts)"
        try { Copy-Item $Tables $TablesClean -Force } catch { }
        return $true
    }
    catch { Write-Host "Столи: заморозити не вийшло ($($_.Exception.Message)) — новий сервер візьме знімок, що пишеться кожні 10 с"; return $false }
}

# back: новий сервер не відповідає й не заморозиться — столи беремо з чистого знімка старого (знятого щойно перед
# перезапуском), а не з того, що новий устиг написати сам: у його знімку партії вже перервані. Старий — не чіпаємо.
function Use-CleanTables {
    if (-not (Test-Path $TablesClean)) { return }
    if (((Get-Date) - (Get-Item $TablesClean).LastWriteTime).TotalMinutes -gt 3) { return }
    Copy-Item $TablesClean $Tables -Force
    Write-Host 'Столи: беру знімок, знятий перед перезапуском'
}

# Caddyfile змінився відтоді, як Caddy його читав, — reload. Caddy переходить на новий конфіг без розриву: потік радіо й
# незакриті відповіді доживають на старому, нові запити йдуть уже за новим.
function Update-Caddy {
    if (-not (Get-Caddy)) { return }
    $sha = (Get-FileHash $Caddyfile -Algorithm SHA256).Hash
    if ((Test-Path $CaddyApplied) -and (Get-Content $CaddyApplied -Raw).Trim() -eq $sha) { return }
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $out = & $Caddy reload --config $Caddyfile --adapter caddyfile 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; Write-Host 'Увага: Caddy не перечитав Caddyfile — працює зі старим'; return }
    Set-Content $CaddyApplied $sha -Encoding ASCII
    Write-Host 'Caddy перечитав Caddyfile'
}

# liquidsoap\.env: ключ для зворотних викликів сервера (RT_API_KEY == Liquidsoap:ApiKey), адреса сервера, порти.
function Read-LiqEnv {
    $vars = [ordered]@{ HARBOR_PORT = '8001'; TELNET_PORT = '1234'; RT_API_URL = 'http://127.0.0.1:8080'; RT_API_KEY = '' }
    if (Test-Path $LiqEnvFile) {
        foreach ($line in Get-Content $LiqEnvFile -Encoding UTF8) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') { $vars[$Matches[1]] = $Matches[2] }
        }
    }
    # .env лишився з часів Docker: сервер тепер на цій же машині, а не на host.docker.internal
    $vars.RT_API_URL = $vars.RT_API_URL -replace 'host\.docker\.internal', '127.0.0.1'
    return $vars
}

# Радіо ніколи не тримає сайт: restart кличе це між Stop-Server і Start-Server, тож будь-яка невдача liquidsoap —
# лише попередження: сервер однаково стартує, а liquidsoap підтягне автонагляд (Watch-Radio).
function Start-Liquidsoap {
    try {
        if (Get-Liquidsoap) { return }
        $v = Read-LiqEnv
        $p = Find-Listener ([int]$v.TELNET_PORT) 'liquidsoap'
        if ($p) { Set-Content $LiqPidFile $p.Id; return }
        if (-not (Test-Path $Liq)) { Write-Host "liquidsoap пропускаю: нема $Liq (його качає setup.ps1)"; return }
        New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null
        # список запаски пише сервер; поки його нема, liquidsoap має за чим стежити
        if (-not (Test-Path $SpareList)) { New-Item -ItemType File $SpareList | Out-Null }
        foreach ($k in $v.Keys) { Set-Item "env:$k" $v[$k] }
        $env:SPARE_PLAYLIST = $SpareList -replace '\\', '/'
        foreach ($f in $LiqLog, $LiqErrLog) {
            try { if ((Test-Path $f) -and (Get-Item $f).Length -gt 0) { Move-Item $f ($f -replace '\.log$', '.prev.log') -Force } } catch { }
        }
        # Робоча тека — tools\liquidsoap: там збірка тримає свій кеш скриптів. Шлях без «\цифра»: liquidsoap на
        # Windows падає, коли в шляху до нього є таке (пастка з 30.09.2026).
        $p = Start-Process -FilePath $Liq -ArgumentList "`"$LiqScript`"" -WorkingDirectory (Split-Path $Liq) `
            -RedirectStandardOutput $LiqLog -RedirectStandardError $LiqErrLog -WindowStyle Hidden -PassThru
        Set-Priority $p   # потік не має заїкатись, коли машину займає збірка чи гра
        Set-Content $LiqPidFile $p.Id
        Write-Host "liquidsoap запущено (pid $($p.Id)), потік: http://127.0.0.1:$($v.HARBOR_PORT)/radio.mp3, лог: $LiqLog"
    }
    catch { Write-Host "Увага: liquidsoap не піднявся ($($_.Exception.Message)) — сервер однаково запускаю, радіо підтягне автонагляд" }
}

function Stop-Liquidsoap {
    # Тут liquidsoap не живе (D:\or-dev, воркдерева): за портом 1234 знайшовся б ЖИВИЙ ефір проду
    if (-not (Test-Path $Liq)) { Write-Host "liquidsoap пропускаю: нема $Liq"; return }
    $p = Get-Liquidsoap
    if (-not $p) { $p = Find-Listener ([int](Read-LiqEnv).TELNET_PORT) 'liquidsoap' }
    if ($p) { Stop-Process -Id $p.Id -Force; Remove-Item $LiqPidFile -ErrorAction SilentlyContinue; Write-Host 'liquidsoap зупинено' }
    else { Write-Host 'liquidsoap не працював' }
}

# Автозапуск і автонагляд — завдання в Планувальнику, як у LeBot: при вході у Windows і далі щохвилини.
# Кожен запуск — коротка перевірка start.ps1 watchdog, тож окремий вічний процес-наглядач не потрібен (і сам не впаде).
function Install-Autostart {
    $ps1 = Join-Path $Root 'start.ps1'
    # wscript ховає вікно повністю (сам powershell -WindowStyle Hidden блимав би консоллю щохвилини) і не чекає на скрипт
    @(
        'Set sh = CreateObject("WScript.Shell")'
        "sh.Run ""powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """"$ps1"""" watchdog"", 0, False"
    ) | Set-Content $Vbs -Encoding ASCII
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "//B //Nologo `"$Vbs`"" -WorkingDirectory $Root
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $user
    $every = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 1)
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $logon, $every -Settings $settings -Principal $principal `
        -Description "Глечики: піднімає сайт після входу у Windows і щохвилини перевіряє, чи ніщо не впало ($ps1 watchdog)" -Force | Out-Null
    # Старий автозапуск (Hlechyky.vbs в автозавантаженні) робив start і змагався б з автонаглядом
    $legacy = Join-Path ([Environment]::GetFolderPath('Startup')) 'Hlechyky.vbs'
    if (Test-Path $legacy) { Remove-Item $legacy -Force }
    Write-Host "Автозапуск і автонагляд встановлено: завдання «$TaskName» у Планувальнику (при вході і щохвилини), лог дій: $WatchLog"
}

# Сервер і Caddy — вище звичайного пріоритету. Планувальник запускає автонагляд (а з ним і деплой, і restart) з
# пріоритетом 7 — BelowNormal, і обидва його успадковували. Поки машина відпочиває, різниці нема; щойно її займає
# збірка, тести, агенти чи гра — цикл тика голодує, і реалтайм-ігри смикаються всім. Заміри 29.09 (аерохокей, 14 з 16
# потоків зайняті): BelowNormal — кадр у середньому раз на 105 мс замість 40, паузи до 0,85 с, 17 тиків пропало за 14 с;
# AboveNormal — рівні 40 ± 2 мс, жодного пропуску. Автонагляд підтягує й уже запущені (Watch-Server, Watch-Caddy).
function Set-Priority($p) {
    if (-not $p) { return }
    try {
        $p.Refresh()
        if ($p.PriorityClass -in 'Idle', 'BelowNormal', 'Normal') { $p.PriorityClass = 'AboveNormal' }
    }
    catch { }
}

function Start-Server {
    if (Get-Server) { Write-Host 'Сервер уже працює'; return }
    if (-not (Test-Path $Dll)) { Invoke-Build }
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null
    # Попередній лог лишаємо поруч: після падіння причина саме там, а новий запуск перезаписав би файл
    foreach ($f in $Log, $ErrLog) {
        try { if ((Test-Path $f) -and (Get-Item $f).Length -gt 0) { Move-Item $f ($f -replace '\.log$', '.prev.log') -Force } } catch { }
    }
    $p = Start-Process -FilePath 'dotnet' -ArgumentList "`"$Dll`"" -WorkingDirectory $Root `
        -RedirectStandardOutput $Log -RedirectStandardError $ErrLog -WindowStyle Hidden -PassThru
    Set-Priority $p
    Set-Content $PidFile $p.Id
    Write-Host "Сервер запущено (pid $($p.Id)), лог: $Log"
}

function Stop-Server {
    $p = Get-Server
    if ($p) {
        Stop-Process -Id $p.Id -Force
        # Чекаємо, поки процес справді зникне: доти він тримає build\ і порт, і підміна теки чи новий старт спіткнулись би
        try { $p.WaitForExit(10000) | Out-Null } catch { }
        Remove-Item $PidFile -ErrorAction SilentlyContinue
        Write-Host 'Сервер зупинено'
    }
    else { Write-Host 'Сервер не працював' }
}

# Caddy: https://hlechyky.pp.ua → сервер :8080, /radio.mp3 → Icecast :8000. Сертифікат Let's Encrypt бере сам (потрібні порти 80/443 ззовні).
function Start-Caddy {
    if (Get-Caddy) { Write-Host 'Caddy уже працює'; return }
    if (-not (Test-Path $Caddy)) { Write-Host "Caddy пропускаю: нема $Caddy (локально він не потрібен; для проду качати https://caddyserver.com/api/download?os=windows&arch=amd64)"; return }
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data\caddy') | Out-Null
    # caddy пише службові рядки в stderr; з ErrorActionPreference=Stop PowerShell вважав би їх помилкою
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $check = & $Caddy validate --config $Caddyfile --adapter caddyfile 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0) { $check | Write-Host; throw 'Caddyfile не проходить перевірку' }
    $p = Start-Process -FilePath $Caddy -ArgumentList 'run', '--config', "`"$Caddyfile`"", '--adapter', 'caddyfile' -WorkingDirectory $Root `
        -RedirectStandardOutput (Join-Path $Root 'logs\caddy.out.log') -RedirectStandardError (Join-Path $Root 'logs\caddy.err.log') -WindowStyle Hidden -PassThru
    Set-Priority $p
    Set-Content $CaddyPidFile $p.Id
    Write-Host "Caddy запущено (pid $($p.Id)), лог: logs\caddy.log"
}

function Stop-Caddy {
    $p = Get-Caddy
    if ($p) { Stop-Process -Id $p.Id -Force; Remove-Item $CaddyPidFile -ErrorAction SilentlyContinue; Write-Host 'Caddy зупинено' }
    else { Write-Host 'Caddy не працював' }
}

# ---------- автонагляд ----------

# Один start/stop/restart/watchdog за раз: інакше автонагляд підняв би старий сервер посеред restart, поки publish
# переписує build\. М'ютекс звільняється сам, навіть якщо процес убили (тоді наступний отримає AbandonedMutexException — це теж «моє»).
function Enter-Launcher([int]$WaitSeconds) {
    $script:mutex = New-Object Threading.Mutex($false, 'Local\Hlechyky-launcher')
    try { return $script:mutex.WaitOne([TimeSpan]::FromSeconds($WaitSeconds)) }
    catch { if ($_.Exception.GetBaseException() -is [Threading.AbandonedMutexException]) { return $true }; throw }
}

function Write-Watch([string]$Message) {
    if ((Test-Path $WatchLog) -and (Get-Item $WatchLog).Length -gt 1MB) { Move-Item $WatchLog ($WatchLog -replace '\.log$', '.prev.log') -Force }
    Add-Content $WatchLog ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message) -Encoding UTF8
}

function Read-WatchState {
    $d = [ordered]@{ lastRun = 0; httpFails = 0; mountMisses = 0; clockMisses = 0; liqClock = 0; liqClockAt = 0; liqRestartAt = 0; liqStarts = @(); serverStarts = @() }
    $s = $null
    try { if (Test-Path $WatchState) { $s = Get-Content $WatchState -Raw | ConvertFrom-Json } } catch { }
    if ($s) { foreach ($k in @($d.Keys)) { if ($null -ne $s.$k) { $d[$k] = $s.$k } } }
    return $d
}

function Test-DeployRunning {
    if (-not (Test-Path $DeployLock)) { return $false }
    # deploy.ps1 тримає файл відкритим без спільного доступу; відкрився — значить, лишився від убитого деплою
    try { [IO.File]::Open($DeployLock, 'Open', 'Read', 'ReadWrite').Dispose(); return $false } catch { return $true }
}

# Годинник ефіру liquidsoap у секундах (telnet «clock.dump»); $null — не відповів. 30.09.2026 на пробі Windows-збірка раз
# «замерзла» мовчки: процес живий, telnet відповідає, порт слухає, а годинник стоїть і потік віддає 0 байт.
function Invoke-LiqTelnet([int]$Port, [string]$Command) {
    $c = New-Object Net.Sockets.TcpClient
    try {
        if (-not $c.ConnectAsync('127.0.0.1', $Port).Wait(3000)) { return $null }
        $s = $c.GetStream()
        $s.ReadTimeout = 3000
        $b = [Text.Encoding]::ASCII.GetBytes("$Command`nquit`n")
        $s.Write($b, 0, $b.Length)
        return (New-Object IO.StreamReader($s)).ReadToEnd()
    }
    catch { return $null }
    finally { $c.Dispose() }
}

function Get-LiqClock([int]$Port) {
    $text = Invoke-LiqTelnet $Port 'clock.dump'
    if ($text -match 'time: ([0-9.]+)s') { return [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) }
    return $null
}

# Знімок завислого ефіру, щоб колись знайти причину: хвіст logs\liquidsoap.log (Start-Liquidsoap перекладе його в
# .prev.log, а наступний перезапуск затре) і clock.dump. logs\liquidsoap.frozen-<час>.log, п'ять найсвіжіших.
function Save-FrozenLog {
    try {
        $dir = Join-Path $Root 'logs'
        $f = Join-Path $dir ('liquidsoap.frozen-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        $tail = if (Test-Path $LiqLog) { @(Get-Content $LiqLog -Tail 400 -Encoding UTF8) } else { @() }
        $dump = Invoke-LiqTelnet ([int](Read-LiqEnv).TELNET_PORT) 'clock.dump'
        Set-Content $f ($tail + '' + '--- clock.dump ---' + $(if ($dump) { $dump } else { '(telnet не відповів)' })) -Encoding UTF8
        Get-ChildItem $dir -Filter 'liquidsoap.frozen-*.log' | Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -ErrorAction SilentlyContinue
    }
    catch { }
}

# liquidsoap: процесу нема — запустити; процес є, а потік (harbor) не слухає 3 хв або годинник ефіру стоїть 2 перевірки
# поспіль — перезапустити. Падає раз у раз (скажімо, помилка в radio.liq) — не молотимо щохвилини, як і з сервером.
function Watch-Radio($st, [long]$now) {
    if (-not (Test-Path $Liq)) { return }
    $v = Read-LiqEnv
    $p = Get-Liquidsoap
    if (-not $p) {
        $p = Find-Listener ([int]$v.TELNET_PORT) 'liquidsoap'
        if ($p) { Set-Content $LiqPidFile $p.Id; Write-Watch "liquidsoap працював без data\liquidsoap.pid (pid $($p.Id)) — підхопив" }
    }
    if ($p) {
        Set-Priority $p
        # годинник мав піти вперед хоч на пів того часу, що минув від минулої перевірки
        $clock = Get-LiqClock ([int]$v.TELNET_PORT)
        $prevClock = [double]$st.liqClock
        $frozen = $null -eq $clock -or ($st.liqClockAt -and $clock -ge $prevClock -and $clock - $prevClock -lt ($now - [long]$st.liqClockAt) / 2)
        $st.liqClock = if ($null -eq $clock) { 0 } else { $clock }
        $st.liqClockAt = $now
        if ($frozen) { $st.clockMisses++ } else { $st.clockMisses = 0 }
        $listening = [bool](Get-NetTCPConnection -State Listen -LocalPort ([int]$v.HARBOR_PORT) -ErrorAction SilentlyContinue)
        if ($listening) { $st.mountMisses = 0 } else { $st.mountMisses++ }
        $mb = [int]($p.PrivateMemorySize64 / 1MB)   # виділене, не робочий набір (див. $LiqMemQuietMB)
        $fat = $false
        if ($mb -ge $LiqMemQuietMB) {
            $n = if ((Invoke-LiqTelnet ([int]$v.TELNET_PORT) 'listeners') -match '^\s*(\d+)') { [int]$Matches[1] } else { -1 }
            $fat = $n -eq 0 -or $mb -ge $LiqMemHardMB
        }
        if ($st.mountMisses -lt 3 -and $st.clockMisses -lt 2 -and -not $fat) { return }
        if ($now - [long]$st.liqRestartAt -lt 300) { return }
        $why = if ($st.clockMisses -ge 2) { "годинник ефіру стоїть ($($st.clockMisses) перевірки поспіль, потік мовчить)" }
            elseif ($fat) { "з'їв $mb МБ пам'яті (витік Windows-збірки)" + $(if ($mb -lt $LiqMemHardMB) { ', а зараз ніхто не слухає' } else { '' }) }
            else { "потік :$($v.HARBOR_PORT) не слухає вже $($st.mountMisses) хв" }
        Write-Watch "liquidsoap працює, але $why — перезапускаю"
        if ($st.clockMisses -ge 2) { Save-FrozenLog }
        Stop-Liquidsoap | Out-Null
        $st.liqRestartAt = $now
        $st.mountMisses = 0
        $st.clockMisses = 0
        $st.liqClockAt = 0
    }
    $recent = @(@($st.liqStarts) | Where-Object { $now - [long]$_ -lt 1800 })
    $last = if ($recent.Count) { [long]($recent | Measure-Object -Maximum).Maximum } else { 0 }
    if ($recent.Count -ge 5 -and $now - $last -lt 600) { return }
    if (-not $p) { Write-Watch 'liquidsoap не працює — запускаю (попередній лог: logs\liquidsoap.prev.log)' }
    Start-Liquidsoap | Out-Null
    $st.liqStarts = @($recent) + $now
    if ($st.liqStarts.Count -ge 5) { Write-Watch "  liquidsoap запускався $($st.liqStarts.Count) разів за пів години — далі пробую раз на 10 хв, дивись logs\liquidsoap.prev.log" }
}

function Watch-Server($st, [long]$now) {
    $p = Get-Server
    if (-not $p) {
        $p = Find-Listener 8080 'dotnet'
        if ($p) { Set-Content $PidFile $p.Id; Write-Watch "сервер працював без data\server.pid (pid $($p.Id)) — підхопив" }
    }
    if ($p) {
        Set-Priority $p
        try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 'http://127.0.0.1:8080/api/me' | Out-Null; $st.httpFails = 0; return }
        catch { $st.httpFails++ }
        if ($st.httpFails -lt 3) { return }
    }

    # Падає раз у раз — не молотимо щохвилини: після 5 запусків за пів години наступна спроба не раніше ніж за 10 хв
    $recent = @(@($st.serverStarts) | Where-Object { $now - [long]$_ -lt 1800 })
    $last = if ($recent.Count) { [long]($recent | Measure-Object -Maximum).Maximum } else { 0 }
    if ($recent.Count -ge 5 -and $now - $last -lt 600) { return }

    if ($p) {
        Write-Watch "сервер (pid $($p.Id)) живий, але $($st.httpFails) хв не відповідає на /api/me — перезапускаю"
        Stop-Server | Out-Null
    }
    else { Write-Watch 'сервер не працює — запускаю (попередній лог: logs\server.prev.log)' }
    $st.httpFails = 0
    Start-Server | Out-Null
    $st.serverStarts = @($recent) + $now
    if ($st.serverStarts.Count -ge 5) { Write-Watch "  сервер запускався $($st.serverStarts.Count) разів за пів години — далі пробую раз на 10 хв, дивись logs\server.prev.log" }
}

function Watch-Caddy {
    if (-not (Test-Path $Caddy)) { return }
    $p = Get-Caddy
    if ($p) { Set-Priority $p; return }
    $p = Find-Listener 443 'caddy'
    if ($p) { Set-Priority $p; Set-Content $CaddyPidFile $p.Id; Write-Watch "Caddy працював без data\caddy.pid (pid $($p.Id)) — підхопив"; return }
    Write-Watch 'Caddy не працює — запускаю'
    Start-Caddy | Out-Null
}

function Invoke-Watchdog {
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null
    $st = Read-WatchState
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $st.lastRun = $now
    try {
        if (Test-Path $StopFlag) { return }      # зупинили руками (stop) — чекаємо start
        if (Test-DeployRunning) { return }       # deploy.ps1 сам перезапускає сервер і сам відкочується
        try { Watch-Radio $st $now } catch { Write-Watch "помилка в перевірці liquidsoap: $_" }
        try { Watch-Server $st $now } catch { Write-Watch "помилка в перевірці сервера: $_" }
        try { Watch-Caddy } catch { Write-Watch "помилка в перевірці Caddy: $_" }
    }
    finally { $st | ConvertTo-Json -Compress | Set-Content $WatchState -Encoding ASCII }
}

# ---------- команди ----------

$locked = $false
if ($Cmd -in 'start', 'stop', 'restart', 'back', 'radio', 'watchdog') {
    $wait = if ($Cmd -eq 'watchdog') { 0 } else { 600 }
    $locked = Enter-Launcher $wait
    if (-not $locked) {
        if ($Cmd -eq 'watchdog') { exit 0 }   # попередня перевірка ще йде
        throw 'Інший start.ps1 (restart, деплой чи автонагляд) працює вже 10 хв, спробуй пізніше'
    }
}

try {
    switch ($Cmd) {
        'build'   { Invoke-Build }
        'start'   { Remove-Item $StopFlag -ErrorAction SilentlyContinue; Start-Liquidsoap; Start-Server; Start-Caddy }
        'stop'    {
            New-Item -ItemType Directory -Force (Join-Path $Root 'data') | Out-Null
            Set-Content $StopFlag (Get-Date -Format 's')
            Invoke-Freeze | Out-Null   # якщо start буде скоро (до 3 хв), столи повернуться, а партії, що вміють зберегтись, — грають далі
            Stop-Caddy; Stop-Server; Stop-Liquidsoap
            Write-Host 'Автонагляд на паузі, доки не буде start'
        }
        'restart' {
            Remove-Item $StopFlag -ErrorAction SilentlyContinue
            Invoke-BuildNext                     # поки старий сервер працює
            Invoke-Freeze | Out-Null
            Stop-Server
            if (-not (Switch-Build)) { Invoke-Build }
            Start-Liquidsoap; Start-Server; Start-Caddy; Update-Caddy
        }
        'back'    {
            Remove-Item $StopFlag -ErrorAction SilentlyContinue
            if (-not (Invoke-Freeze 3)) { Use-CleanTables }
            Stop-Server; Restore-PrevBuild; Start-Liquidsoap; Start-Server; Start-Caddy
        }
        'radio'   {
            if ($Why -and (Test-Path $Liq)) { Save-FrozenLog; Write-Watch "liquidsoap: $Why — перезапускаю" }
            Stop-Liquidsoap; Start-Sleep 1; Start-Liquidsoap
        }
        'status'  {
            $p = Get-Server
            Write-Host ("Сервер:     " + $(if ($p) { "працює (pid $($p.Id))" } else { 'зупинений' }))
            $c = Get-Caddy
            Write-Host ("Caddy:      " + $(if ($c) { "працює (pid $($c.Id)), https://hlechyky.pp.ua" } else { 'зупинений' }))
            $l = Get-Liquidsoap
            $hp = (Read-LiqEnv).HARBOR_PORT
            Write-Host ("liquidsoap: " + $(if ($l) { "працює (pid $($l.Id)), потік http://127.0.0.1:$hp/radio.mp3" } elseif (Test-Path $Liq) { 'зупинений' } else { "нема $Liq (setup.ps1)" }))
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            $last = [long](Read-WatchState).lastRun
            $watch = if (-not $task) { 'не встановлено (start.ps1 autostart)' }
                elseif ($task.State -eq 'Disabled') { 'завдання в Планувальнику вимкнене' }
                elseif (Test-Path $StopFlag) { 'на паузі після stop (start знімає)' }
                elseif ($last) { "працює, остання перевірка $([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - $last) с тому, лог: logs\watchdog.log" }
                else { 'встановлено, ще не перевіряв' }
            Write-Host ("Нагляд:     " + $watch)
        }
        'logs'      { Get-Content $Log -Tail 40 }
        'autostart' { Install-Autostart }
        'watchdog'  { Invoke-Watchdog }
    }
}
finally { if ($locked) { $script:mutex.ReleaseMutex() } }
