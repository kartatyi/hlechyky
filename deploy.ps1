<#
.SYNOPSIS
  Глечики — автодеплой. Підтягує main з GitHub, перевіряє збірку і перезапускає сервер.

  Зазвичай його смикає сам сервер: GitHub шле подію workflow_run на /api/github/deploy,
  сервер перевіряє підпис і запускає цей скрипт окремим процесом (тому скрипт переживає
  власний перезапуск сервера). Руками теж можна: powershell -File deploy.ps1

  Порядок: fetch → нема чого робити? вихід → незакомічені зміни? вихід (нічого не чіпаємо)
         → pull --ff-only → пробна збірка (сервер ще працює) → start.ps1 restart → перевірка /api/me.
  Будь-яка невдача після pull → git reset --hard на попередній коміт і назад на старий код.

  Перезапуск потрібен і тоді, коли код уже тут (власник закомітив локально й запушив):
  data\built.sha каже, з якого коміту зібрано те, що лежить у build\ — її пише start.ps1.

  Усе пишеться в logs\deploy.log.
#>
param(
    # Коміт, через який прилетів вебхук — лише для логу (тягнемо завжди верхівку origin/main).
    [string]$Sha = '',
    # Деплоїти навіть із незакомієченими змінами в робочій копії (вони поїдуть на прод як є).
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Start = Join-Path $Root 'start.ps1'
$LogFile = Join-Path $Root 'logs\deploy.log'
$LockFile = Join-Path $Root 'data\deploy.lock'
$BuiltFile = Join-Path $Root 'data\built.sha'
$Branch = 'main'

New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null

function Write-Log([string]$Message) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    Write-Host $line
}

function Short([string]$Sha) { if ($Sha.Length -gt 7) { $Sha.Substring(0, 7) } else { $Sha } }

# Нативні команди пишуть службові рядки в stderr; з ErrorActionPreference=Stop PowerShell вважав би їх помилкою.
function Invoke-Step {
    param([string]$File, [string[]]$Arguments, [switch]$AllowFail)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & $File @Arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    foreach ($line in $out) { if ($line.Trim()) { Write-Log "    $line" } }
    if ($code -ne 0 -and -not $AllowFail) { throw "$File $($Arguments -join ' ') → код $code" }
    return $code
}

function Get-Git([string[]]$Arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & git -C $Root @Arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) { throw "git $($Arguments -join ' ') → код $code`n$($out -join "`n")" }
    return ($out -join "`n").Trim()
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

# Сервер піднявся? /api/me — найдешевша відповідь, яку він уміє.
function Test-Server([int]$TimeoutSeconds = 90) {
    $url = "http://127.0.0.1:$(Get-ListenPort)/api/me"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 $url | Out-Null
            return $true
        } catch { Start-Sleep 3 }
    }
    return $false
}

function Invoke-Restart { Invoke-Step powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Start, 'restart') }

function Invoke-Rollback([string]$To, [string]$Why, [bool]$ServerTouched) {
    Write-Log "ВІДКАТ на $(Short $To) — $Why"
    try {
        Invoke-Step git @('-C', $Root, 'reset', '--hard', $To) | Out-Null
        if ($ServerTouched) {
            Invoke-Restart | Out-Null
            if (Test-Server) { Write-Log 'Відкотилися, сайт живий' } else { Write-Log 'ЛИХО: після відкату сервер не піднявся, дивись logs\server.log' }
        } else {
            Write-Log 'Сервер не чіпали, він і далі крутить старий код'
        }
    } catch {
        Write-Log "ЛИХО: відкат не вдався — $($_.Exception.Message)"
    }
}

# Один деплой за раз: другий вебхук, що прилетів слідом, просто йде геть.
$lock = $null
try { $lock = [System.IO.File]::Open($LockFile, 'Create', 'Write', 'None') }
catch { Write-Log 'Деплой уже йде, пропускаю'; return }

$restartAttempted = $false
$before = $null
try {
    $before = Get-Git @('rev-parse', 'HEAD')
    Invoke-Step git @('-C', $Root, 'fetch', '--quiet', 'origin', $Branch) | Out-Null
    $target = Get-Git @('rev-parse', "origin/$Branch")
    $built = if (Test-Path $BuiltFile) { (Get-Content $BuiltFile -Raw).Trim() } else { '' }

    $needPull = $before -ne $target
    $needRestart = $built -ne $target   # порожня позначка = невідомо, з чого зібрано build\ — краще перезібрати

    if (-not $needPull -and -not $needRestart) {
        Write-Log "Нічого нового (HEAD = $(Short $before))"
        return
    }

    # Незакомічене чіпати не можна: merge його зіб'є, а restart зібрав би прод із недоробленого.
    $dirty = Get-Git @('status', '--porcelain')
    if ($dirty -and -not $Force) {
        Write-Log 'СТОП: у робочій копії є незакомічені зміни, нічого не чіпаю:'
        foreach ($line in $dirty -split "`n") { if ($line.Trim()) { Write-Log "    $line" } }
        Write-Log 'Закоміть або сховай їх (git stash) — і наступний коміт деплой підхопить сам. Дуже треба зараз: deploy.ps1 -Force'
        return
    }

    if ($needPull) {
        $incoming = Get-Git @('log', '--oneline', '--no-decorate', "$before..$target")
        Write-Log "Новий код у origin/$Branch$(if ($Sha) { " (вебхук про $(Short $Sha))" }):"
        foreach ($line in $incoming -split "`n") { if ($line.Trim()) { Write-Log "    $line" } }
        Invoke-Step git @('-C', $Root, 'merge', '--ff-only', "origin/$Branch") | Out-Null
        Write-Log "Підтягнув $(Short $target)"
    } else {
        Write-Log "Код уже тут ($(Short $target)), але в build\ лежить $(if ($built) { Short $built } else { 'невідомо що' }) — перезбираю"
    }

    # Пробна збірка, поки старий сервер працює: build\ зайнятий ним, тому збираємо в bin\ (як це робить CI).
    Write-Log 'Пробна збірка…'
    try { Invoke-Step dotnet @('build', (Join-Path $Root 'src\Hlechyky\Hlechyky.csproj'), '-c', 'Release', '--nologo', '-v', 'q') | Out-Null }
    catch { Invoke-Rollback $before "збірка впала: $($_.Exception.Message)" $false; return }

    Write-Log 'Перезапускаю сервер…'
    $restartAttempted = $true
    Invoke-Restart | Out-Null

    if (Test-Server) { Write-Log "ГОТОВО: сайт на $(Short $target)" }
    else { Invoke-Rollback $before 'сервер не відповів після перезапуску' $true }
} catch {
    Write-Log "ПОМИЛКА: $($_.Exception.Message)"
    if ($before -and (Get-Git @('rev-parse', 'HEAD')) -ne $before) { Invoke-Rollback $before 'щось пішло не так' $restartAttempted }
} finally {
    if ($lock) { $lock.Dispose(); Remove-Item $LockFile -ErrorAction SilentlyContinue }
}
