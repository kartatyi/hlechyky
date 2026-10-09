<#
.SYNOPSIS
  Глечики — тести. За замовчуванням ганяє лише «свої» тести — ті, що пишуться під задачу: класи з тестових
  файлів, змінених у цій гілці (незакомічене, нові файли й коміти, яких ще нема в базі). Решта набору (~7 тис.)
  пропускається.

  test.ps1                — свої тести
  test.ps1 Clicker Bets   — тести, у повній назві яких є Clicker або Bets (вираз з = ~ ! іде як є: "Category=Perf")
  test.ps1 -All           — увесь набір: перед злиттям / пушем, після великого шматка роботи або на прохання
  test.ps1 -List          — лише показати, що запустилось би
  test.ps1 -Base <ref>    — з чим порівнювати гілку (за замовчуванням: origin/main для main, main для решти)

  Зі «своїх» беруться лише файли, де є [Fact] чи [Theory]; змінені помічники й фікстури самі нічого не запускають.
#>
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Name,
    [switch]$All,
    [switch]$List,
    [string]$Base
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
# Кирилиця в конвеєрі (Bash, tail) інакше лягає як «??????».
try { [Console]::OutputEncoding = [Text.UTF8Encoding]::new() } catch { }
$Proj = 'tests/Hlechyky.Tests'

# Не «Git»: PowerShell не розрізняє регістр, і функція перекрила б сам git.exe.
function Invoke-Git([string[]]$GitArgs) {
    # Windows PowerShell 5.1 з 'Stop' робить зі stderr git виняток навіть при 2>$null.
    $ErrorActionPreference = 'Continue'
    $out = & git.exe -C $Root @GitArgs 2>$null
    if ($LASTEXITCODE -ne 0) { return @() }
    @($out | Where-Object { $_ })
}

# Свої тести: класи з тестових файлів, змінених відносно точки розгалуження з базою.
function Get-MineClasses {
    if (-not $Base) {
        $branch = (Invoke-Git @('branch', '--show-current')) | Select-Object -First 1
        $Base = if ($branch -eq 'main' -or -not $branch) { 'origin/main' } else { 'main' }
    }
    $fork = (Invoke-Git @('merge-base', 'HEAD', $Base)) | Select-Object -First 1
    if (-not $fork) { throw "Не знайшов спільного коміту з $Base — дай -Base <гілка>" }
    $script:BaseShown = "$Base @ $($fork.Substring(0, 7))"

    $files = @(Invoke-Git @('diff', '--name-only', '--diff-filter=ACMR', $fork, '--', $Proj)) +
             @(Invoke-Git @('ls-files', '--others', '--exclude-standard', '--', $Proj))
    $script:SrcChanged = @(Invoke-Git @('diff', '--name-only', $fork, '--', 'src', 'web')).Count +
                         @(Invoke-Git @('ls-files', '--others', '--exclude-standard', '--', 'src', 'web')).Count

    $classes = New-Object System.Collections.Generic.List[string]
    foreach ($f in ($files | Where-Object { $_ -like '*.cs' } | Sort-Object -Unique)) {
        $text = Get-Content (Join-Path $Root $f) -Raw -Encoding UTF8
        if ($text -notmatch '\[(Fact|Theory)\b') { continue }
        foreach ($m in [regex]::Matches($text, '(?m)^\s*(?:(?:public|internal|private|sealed|static|partial|abstract)\s+)*class\s+(\w+)')) {
            if (-not $classes.Contains($m.Groups[1].Value)) { $classes.Add($m.Groups[1].Value) }
        }
    }
    , $classes
}

$filter = $null
if ($All) {
    $what = 'увесь набір'
} elseif ($Name) {
    $filter = ($Name | ForEach-Object { if ($_ -match '[=~!]') { $_ } else { "FullyQualifiedName~$_" } }) -join '|'
    $what = "за назвою: $($Name -join ', ')"
} else {
    $classes = Get-MineClasses
    if ($classes.Count -eq 0) {
        Write-Host "Своїх тестів нема: у $Proj нічого не змінено відносно $BaseShown."
        if ($SrcChanged) { Write-Host "Код змінено ($SrcChanged файл.) — напиши тест під задачу або дай назву: test.ps1 Clicker" }
        Write-Host 'Увесь набір — test.ps1 -All (перед злиттям, після великого шматка або на прохання).'
        exit 0
    }
    # Повна назва — Простір.Клас.Метод, вкладений клас — Простір.Зовнішній+Клас.Метод.
    $filter = ($classes | ForEach-Object { "FullyQualifiedName~.$_.|FullyQualifiedName~+$_." }) -join '|'
    $what = "свої ($($classes.Count) кл., відносно $BaseShown): $($classes -join ', ')"
}

Write-Host "Тести — $what"
if ($List) { if ($filter) { Write-Host "--filter `"$filter`"" }; exit 0 }

# ErrorsOnly ховає ~35 старих попереджень збірки (помилки компіляції видно); minimal — по червоних
# тестах повідомлення й стек, зелені не перелічує.
$dotnetArgs = @('test', (Join-Path $Root $Proj), '--nologo', '-nodeReuse:false', '-v', 'q', '-clp:ErrorsOnly',
    '--logger', 'console;verbosity=minimal')
if ($filter) { $dotnetArgs += @('--filter', $filter) }
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet @dotnetArgs
$code = $LASTEXITCODE
Write-Host ("Тести: {0}, {1:N0} с" -f $(if ($code -eq 0) { 'зелені' } else { "ЧЕРВОНІ (код $code)" }), $sw.Elapsed.TotalSeconds)
exit $code
