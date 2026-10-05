<#
.SYNOPSIS
  Глечики — деплой. Підтягує main з GitHub, перевіряє збірку і перезапускає сервер.

  З 02.10.2026 — лише по команді: deploy.cmd або powershell -File deploy.ps1. Автодеплой (вебхук
  workflow_run на /api/github/deploy і опитувач GitHub) вимкнено рядком Deploy:Enabled = false в
  appsettings.Local.json: пуш у main більше не перезапускає сайт посеред партій. Увімкнеш назад —
  сервер знову запускатиме цей скрипт сам, окремим процесом (тому він переживає власний перезапуск сервера).

  Порядок: fetch → нема чого робити? вихід → незакомічені зміни чи коміти, яких нема на GitHub? вихід
         (нічого не чіпаємо) → збірка в build.next з окремої копії коду (сервер працює, web\ на проді не чіпається)
         → чекаємо паузи: поки за столами йде партія, яку перезапуск перервав би (до -WaitMinutes, далі — питати
         людину) → pull --ff-only → start.ps1 restart (столи в знімок, підміна теки, ~2 с) → перевірка /api/me.
  Збірка впала — нічого не чіпали. Невдача після pull → git reset --hard і попередня збірка назад (start.ps1 back).

  Перезапуск потрібен і тоді, коли код уже тут (власник закомітив локально й запушив):
  data\built.sha каже, з якого коміту зібрано те, що лежить у build\ — її пише start.ps1.

  Усе пишеться в logs\deploy.log.
#>
param(
    # Коміт, через який прилетів вебхук: тягнемо завжди верхівку origin/main, а цей лише звіряємо з build\ і пишемо в лог.
    [string]$Sha = '',
    # Хто запустив: hand — руками, webhook — подія від GitHub, poll — сервер сам побачив зелену збірку, вебхук про яку не дійшов.
    [string]$Via = 'hand',
    # Деплоїти як є: із незакоміченими змінами в робочій копії чи комітами, яких ще нема на GitHub.
    [switch]$Force,
    # Не чекати паузи: партії, що йдуть, перерве (ставки повернуться; ігри, що вміють зберегтись, грають далі).
    [switch]$Now,
    # Скільки чекати паузи, поки за столами грають. Не дочекались — нічого не чіпаємо й кажемо, хто грає (код виходу 3).
    [int]$WaitMinutes = 10
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Start = Join-Path $Root 'start.ps1'
$LogFile = Join-Path $Root 'logs\deploy.log'
$LockFile = Join-Path $Root 'data\deploy.lock'
$BuiltFile = Join-Path $Root 'data\built.sha'
$TriedFile = Join-Path $Root 'data\deploy.tried'
$WaitFile = Join-Path $Root 'data\deploy.waiting'
$NextDir = Join-Path $Root 'build.next'
$NextFile = Join-Path $Root 'data\next.sha'
$ControlKey = Join-Path $Root 'data\control.key'
# Окрема копія коду (git worktree) поруч із прод-копією: з неї збирається build.next. Збирати з D:\or не можна —
# туди треба спершу підтягнути код, а web\ прод віддає прямо звідти: клієнт на хвилини випередив би сервер.
$BuildSrc = Join-Path (Split-Path $Root -Parent) ((Split-Path $Root -Leaf) + '-deploy')
$Branch = 'main'

New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null

# Лог не має права вбити деплой: файл на мить може тримати хтось інший (tail -f, «Get-Content -Wait»,
# антивірус), а з ErrorActionPreference = 'Stop' перший же Add-Content викидав скрипт ще до замка —
# деплой мовчки не відбувався, хоч позначку про спробу (data\deploy.tried) сервер уже поставив,
# тому й повторів не було. Кілька спроб, далі пишемо лише в консоль і робимо своє.
function Write-Log([string]$Message) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    for ($i = 0; $i -lt 10; $i++) {
        try { Add-Content -Path $LogFile -Value $line -Encoding UTF8 -ErrorAction Stop; return }
        catch { Start-Sleep -Milliseconds 200 }
    }
}

function Short([string]$Sha) { if ($Sha.Length -gt 7) { $Sha.Substring(0, 7) } else { $Sha } }

# Вивід підпроцесів читаємо з файлів, а не з конвеєра: на Windows онуки успадковують хендли,
# тому «& щось | ForEach-Object» висить вічно, якщо процес лишив по собі демона (сервер, MSBuild-ноду).
# Демон, якого лишив по собі підпроцес, тримає й ці файли: читаємо з дозволом на спільний доступ.
function Read-Output([string]$Path) {
    if (-not (Test-Path $Path)) { return '' }
    $bytes = $null
    try {
        $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $ms = New-Object IO.MemoryStream; $fs.CopyTo($ms); $bytes = $ms.ToArray() } finally { $fs.Dispose() }
    } catch { return '' }
    if (-not $bytes -or $bytes.Length -eq 0) { return '' }
    $strict = [Text.Encoding]::GetEncoding('utf-8', [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
    try { return $strict.GetString($bytes) } catch { return [Console]::OutputEncoding.GetString($bytes) }
}

# Імена тимчасових файлів унікальні, бо старі може ще тримати живий сервер; прибираємо як вийде.
$script:toolStep = 0
Get-ChildItem (Join-Path $Root 'data') -Filter 'deploy.*.tmp' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

function Invoke-Tool {
    param([string]$File, [string[]]$Arguments)
    $script:toolStep++
    $out = Join-Path $Root ('data\deploy.{0}.{1}.out.tmp' -f $PID, $script:toolStep)
    $err = Join-Path $Root ('data\deploy.{0}.{1}.err.tmp' -f $PID, $script:toolStep)
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $p = Start-Process -FilePath $File -ArgumentList $quoted -WorkingDirectory $Root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $p.Handle   # без цього дотику ExitCode лишиться порожнім (пастка Start-Process -PassThru)
    $p.WaitForExit()    # саме цього процесу, не всього дерева
    $text = (((Read-Output $out) + "`n" + (Read-Output $err)) -replace "`r", '').Trim()
    Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Code = $p.ExitCode; Text = $text }
}

function Invoke-Step {
    param([string]$File, [string[]]$Arguments, [switch]$AllowFail)
    $r = Invoke-Tool $File $Arguments
    foreach ($line in $r.Text -split "`n") { if ($line.Trim()) { Write-Log "    $($line.TrimEnd())" } }
    if ($r.Code -ne 0 -and -not $AllowFail) { throw "$File $($Arguments -join ' ') → код $($r.Code)" }
    return $r.Code
}

function Get-Git([string[]]$Arguments) {
    $r = Invoke-Tool 'git' (@('-C', $Root) + $Arguments)
    if ($r.Code -ne 0) { throw "git $($Arguments -join ' ') → код $($r.Code)`n$($r.Text)" }
    return $r.Text.Trim()
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
function Test-Server([int]$TimeoutSeconds = 60) {
    $url = "http://127.0.0.1:$(Get-ListenPort)/api/me"
    $pidFile = Join-Path $Root 'data\server.pid'
    $started = Get-Date
    $deadline = $started.AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 $url | Out-Null
            return $true
        } catch { Start-Sleep -Milliseconds 500 }
        # Сервер упав на старті (виняток у Program.cs, битий конфіг) — чекати хвилину нема чого: відкат одразу.
        $alive = (Test-Path $pidFile) -and (Get-Process -Id ([int](Get-Content $pidFile -Raw).Trim()) -ErrorAction SilentlyContinue)
        if (-not $alive -and ((Get-Date) - $started).TotalSeconds -gt 3) { return $false }
    }
    return $false
}

# -Command замість -File лише заради [Console]::OutputEncoding: інакше кирилиця зі start.ps1 лягає в лог як «??????».
function Invoke-Launcher([string]$Cmd) { Invoke-Step powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "& { [Console]::OutputEncoding = [Text.UTF8Encoding]::new(); & '$Start' $Cmd }") }
function Invoke-Restart { Invoke-Launcher 'restart' }

function Invoke-Rollback([string]$To, [string]$Why, [bool]$ServerTouched) {
    Write-Log "ВІДКАТ на $(Short $To) — $Why"
    try {
        Invoke-Step git @('-C', $Root, 'reset', '--hard', $To) | Out-Null
        if ($ServerTouched) {
            # Попередня збірка ще лежить у build.prev — назад за секунду; її нема — збираємо стару наново.
            try { Invoke-Launcher 'back' | Out-Null } catch { Write-Log "    build.prev не підійшов ($($_.Exception.Message)) — збираю старий код"; Invoke-Restart | Out-Null }
            if (Test-Server) { Write-Log 'Відкотилися, сайт живий' } else { Write-Log 'ЛИХО: після відкату сервер не піднявся, дивись logs\server.log' }
        } else {
            Write-Log 'Сервер не чіпали, він і далі крутить старий код'
        }
    } catch {
        Write-Log "ЛИХО: відкат не вдався — $($_.Exception.Message)"
    }
}

# Збірка коміту $Sha в build.next, поки сервер працює. Код — з окремої копії ($BuildSrc), прод-копія не чіпається.
# Уже зібрано (data\next.sha) — нічого не робимо: деплой, що не дочекався паузи, вдруге не збирає.
# -FromRoot — з робочої копії як є (deploy.ps1 -Force при незакомічених змінах: інакше вони поїхали б у web\, а не в сервер).
function Invoke-Prepare([string]$Sha, [switch]$FromRoot) {
    if ($FromRoot) {
        Write-Log "Збираю робочу копію як є у build.next (-Force)…"
        if (Test-Path $NextDir) { Remove-Item $NextDir -Recurse -Force }
        Invoke-Step dotnet @('publish', (Join-Path $Root 'src\Hlechyky\Hlechyky.csproj'), '-c', 'Release', '-o', $NextDir, '--nologo', '-v', 'q', '-nodeReuse:false') | Out-Null
        Set-Content $NextFile $Sha -Encoding ASCII
        return
    }
    if ((Test-Path (Join-Path $NextDir 'Hlechyky.dll')) -and (Test-Path $NextFile) -and (Get-Content $NextFile -Raw).Trim() -eq $Sha) {
        Write-Log "Збірка $(Short $Sha) уже готова (build.next)"
        return
    }
    if (-not (Test-Path (Join-Path $BuildSrc '.git'))) {
        Invoke-Step git @('-C', $Root, 'worktree', 'prune') -AllowFail | Out-Null
        Invoke-Step git @('-C', $Root, 'worktree', 'add', '--detach', '--force', $BuildSrc, $Sha) | Out-Null
    } else {
        Invoke-Step git @('-C', $BuildSrc, 'checkout', '--detach', '--force', $Sha) | Out-Null
        Invoke-Step git @('-C', $BuildSrc, 'clean', '-fdq') | Out-Null   # bin\ і obj\ у .gitignore — лишаються, збірка інкрементна
    }
    Write-Log "Збираю $(Short $Sha) у build.next (сервер працює)…"
    if (Test-Path $NextDir) { Remove-Item $NextDir -Recurse -Force }
    Remove-Item $NextFile -Force -ErrorAction SilentlyContinue
    Invoke-Step dotnet @('publish', (Join-Path $BuildSrc 'src\Hlechyky\Hlechyky.csproj'), '-c', 'Release', '-o', $NextDir, '--nologo', '-v', 'q', '-nodeReuse:false') | Out-Null
    Set-Content $NextFile $Sha -Encoding ASCII
}

# Хто зараз грає так, що перезапуск це перервав би (GET /api/internal/busy). $null — сервер не сказав (лежить чи ще старий).
function Get-Busy {
    if (-not (Test-Path $ControlKey)) { return $null }
    try {
        $key = (Get-Content $ControlKey -Raw).Trim()
        $r = Invoke-RestMethod -Uri "http://127.0.0.1:$(Get-ListenPort)/api/internal/busy" -Headers @{ 'X-Control-Key' = $key } -TimeoutSec 5
        return ,@($r.busy)   # кома: інакше порожній список PowerShell розгорне в $null — «сервер не сказав»
    } catch { return $null }
}

function Format-Busy($busy) {
    ($busy | ForEach-Object {
        $mins = if ($_.since) { [int][math]::Floor(((Get-Date).ToUniversalTime() - ([DateTimeOffset]$_.since).UtcDateTime).TotalMinutes) } else { 0 }
        "$($_.title) — $(@($_.players) -join ', ') ($mins хв)"
    }) -join '; '
}

# Чекаємо, поки не лишиться партій, які перезапуск перервав би. $true — можна; $false — не дочекались за $Minutes.
function Wait-Pause([int]$Minutes) {
    $deadline = (Get-Date).AddMinutes($Minutes)
    $said = ''
    while ($true) {
        $busy = Get-Busy
        if ($null -eq $busy) { Write-Log 'Сервер не каже, хто грає (старий чи лежить) — не чекаю'; return $true }
        if ($busy.Count -eq 0) {
            if ($said) { Write-Log 'Пауза — усі столи між партіями' }
            return $true
        }
        $now = Format-Busy $busy
        if ($now -ne $said) { Write-Log "Чекаю паузи, бо грають: $now"; $said = $now }
        if ((Get-Date) -ge $deadline) {
            Write-Log "НЕ ДОЧЕКАВСЯ ПАУЗИ за $Minutes хв — грають: $now"
            return $false
        }
        Start-Sleep -Seconds 3
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

    # Подія про збірку, яка давно в проді, — не привід нічого чіпати. 02.10.2026 о 02:47 GitHub API раз віддав
    # «найсвіжішою зеленою» збірку 1bb2b38 від 17.09, опитувач повірив — і сайт перезапустився посеред ночі.
    if ($Sha -and $built -and (Invoke-Tool 'git' @('-C', $Root, 'merge-base', '--is-ancestor', $Sha, $built)).Code -eq 0) {
        Write-Log "Збірка $(Short $Sha) стара — вона вже є в проді ($(Short $built)), нічого не чіпаю ($Via)"
        return
    }

    # Локальна копія буває попереду origin (закомітили тут, а запушити ще не встигли): тягнути тоді нічого.
    # Викочувати HEAD у такому разі теж не можна — див. СТОП нижче.
    $ahead = $before -ne $target -and (Invoke-Tool 'git' @('-C', $Root, 'merge-base', '--is-ancestor', $target, $before)).Code -eq 0
    if ($ahead) { $target = $before }

    $needPull = $before -ne $target
    $needRestart = $built -ne $target   # порожня позначка = невідомо, з чого зібрано build\ — краще перезібрати

    if (-not $needPull -and -not $needRestart) {
        Write-Log "Нічого нового (HEAD = $(Short $before)$(if ($ahead) { ', попереду origin' })$(if ($Sha) { "; $Via про $(Short $Sha)" }))"
        return
    }

    # На прод іде лише те, що вже є на GitHub. 02.10.2026 сюди закомітили 211016f і не запушили, а деплой,
    # смикнутий старою збіркою, викотив його: прод розійшовся з origin, і будь-який пуш звідкись іще або не ліг би
    # fast-forward, або тихо затер би те, що крутиться на сайті. Спершу git push, потім деплой.
    if ($ahead -and -not $Force) {
        $local = Get-Git @('log', '--oneline', '--no-decorate', "origin/$Branch..$before")
        Write-Log "СТОП: у $Branch є коміти, яких нема на GitHub, — не викочую. Спершу git push (дуже треба як є: deploy.ps1 -Force):"
        foreach ($line in $local -split "`n") { if ($line.Trim()) { Write-Log "    $($line.TrimEnd())" } }
        return
    }

    # Незакомічене чіпати не можна: merge його зіб'є, а restart зібрав би прод із недоробленого.
    # Але це не провал деплою, а «не зараз»: позначку про спробу (data\deploy.tried) знімаємо, щоб опитувач
    # повернувся до цього коміту, щойно робоча копія стане чистою. Без цього одна відмова ховала зелену
    # збірку до самого наступного пуша, і виглядало це так, ніби автодеплою просто нема.
    # Щоб лог не заповнювався тим самим щотри хвилини, повний список пишемо лише коли він змінився.
    $dirty = Get-Git @('status', '--porcelain')
    if ($dirty -and -not $Force) {
        $mark = "$target`n$dirty"
        $seen = ''
        if (Test-Path $WaitFile) { $seen = [string](Get-Content $WaitFile -Raw -Encoding UTF8) }   # порожній файл дає $null
        if ($seen.Trim() -ne $mark.Trim()) {
            Write-Log "СТОП: у робочій копії є незакомічені зміни, нічого не чіпаю (чекає $(Short $target)):"
            foreach ($line in $dirty -split "`n") { if ($line.Trim()) { Write-Log "    $($line.TrimEnd())" } }
            Write-Log 'Закоміть або сховай їх (git stash) — і деплой підхопить сам, без нового пуша. Дуже треба зараз: deploy.ps1 -Force'
            try { Set-Content $WaitFile $mark -Encoding UTF8 } catch { }
        }
        Remove-Item $TriedFile -Force -ErrorAction SilentlyContinue
        return
    }
    Remove-Item $WaitFile -Force -ErrorAction SilentlyContinue

    if ($needPull) {
        $incoming = Get-Git @('log', '--oneline', '--no-decorate', "$before..$target")
        Write-Log "Новий код у origin/$Branch$(if ($Sha) { " ($Via про $(Short $Sha))" }):"
        foreach ($line in $incoming -split "`n") { if ($line.Trim()) { Write-Log "    $($line.TrimEnd())" } }
    } else {
        Write-Log "Код уже тут ($(Short $target)), але в build\ лежить $(if ($built) { Short $built } else { 'невідомо що' }) — перезбираю"
    }

    # -Force із незакоміченими змінами: вони в робочій копії, тож і код тягнемо, і збираємо саме там. web\ тоді новий
    # уже зараз, тож і паузи не чекаємо — інакше клієнт хвилинами випереджав би сервер.
    $asIs = $Force -and $dirty
    if ($asIs) {
        if ($needPull) { Invoke-Step git @('-C', $Root, 'merge', '--ff-only', $target) | Out-Null; Write-Log "Підтягнув $(Short $target) (-Force, з незакоміченими)" }
        try { Invoke-Prepare $target -FromRoot }
        catch { Invoke-Rollback $before "збірка впала: $($_.Exception.Message)" $false; exit 2 }
    } else {
        # Збірка поруч, поки старий сервер працює. Впала — прод ніхто не чіпав: ні код, ні сервер.
        try { Invoke-Prepare $target }
        catch { Write-Log "ЗБІРКА ВПАЛА — нічого не чіпаю, сайт і далі на $(Short $built): $($_.Exception.Message)"; exit 2 }

        # Пауза: перезапуск посеред партії її перервав би. Ігри, що вміють зберегтись, сервер сюди й не записує.
        if (-not $Now -and -not (Wait-Pause $WaitMinutes)) {
            Write-Log "Нічого не чіпав: збірка $(Short $target) лежить готова. Ще почекати — deploy.ps1; перервати партії — deploy.ps1 -Now"
            Remove-Item $TriedFile -Force -ErrorAction SilentlyContinue   # «не зараз», а не провал: опитувач (якщо його ввімкнуть) спробує знову
            exit 3
        }
    }

    # Тепер — швидко: код (і з ним web\) і сервер міняються в одну мить.
    if ($needPull -and -not $asIs) {
        if (-not $Force -and (Get-Git @('status', '--porcelain'))) { Write-Log 'СТОП: поки чекав, у робочій копії з''явились незакомічені зміни — нічого не чіпаю'; exit 1 }
        Invoke-Step git @('-C', $Root, 'merge', '--ff-only', $target) | Out-Null
        Write-Log "Підтягнув $(Short $target)"
    }
    Write-Log "Перезапускаю сервер$(if ($Now) { ' (-Now: партії, що йдуть, перерве)' })…"
    $restartAttempted = $true
    Invoke-Restart | Out-Null

    if (Test-Server) { Write-Log "ГОТОВО: сайт на $(Short $target)" }
    else { Invoke-Rollback $before 'сервер не відповів після перезапуску' $true; exit 1 }
} catch {
    Write-Log "ПОМИЛКА: $($_.Exception.Message)"
    if ($before -and (Get-Git @('rev-parse', 'HEAD')) -ne $before) { Invoke-Rollback $before 'щось пішло не так' $restartAttempted }
    exit 1
} finally {
    if ($lock) { $lock.Dispose(); Remove-Item $LockFile -ErrorAction SilentlyContinue }
}
