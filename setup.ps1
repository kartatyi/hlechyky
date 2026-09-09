<#
.SYNOPSIS
  Глечики — перший запуск після git clone. Качає yt-dlp і ffmpeg у tools\, створює appsettings.Local.json і liquidsoap\.env
  з випадковими ключами (ті два файли в .gitignore) і великий словник для Ерудита в data\words\.
  Запускати можна скільки завгодно: те, що вже є, не чіпає.

  powershell -ExecutionPolicy Bypass -File setup.ps1
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # без прогрес-бару Invoke-WebRequest у PowerShell 5 качає в рази швидше
$Root = $PSScriptRoot

function New-Key([int]$Bytes) {
    $b = New-Object byte[] $Bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    ($b | ForEach-Object { $_.ToString('x2') }) -join ''
}

# 1. Інструменти: yt-dlp.exe, ffmpeg.exe, ffprobe.exe у tools\yt-dlp\
$yt = Join-Path $Root 'tools\yt-dlp'
New-Item -ItemType Directory -Force $yt | Out-Null
if (Test-Path (Join-Path $yt 'yt-dlp.exe')) { Write-Host 'yt-dlp.exe вже є' }
else {
    Write-Host 'Качаю yt-dlp.exe…'
    Invoke-WebRequest -UseBasicParsing 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile (Join-Path $yt 'yt-dlp.exe')
}
if ((Test-Path (Join-Path $yt 'ffmpeg.exe')) -and (Test-Path (Join-Path $yt 'ffprobe.exe'))) { Write-Host 'ffmpeg.exe і ffprobe.exe вже є' }
else {
    Write-Host 'Качаю ffmpeg (збірка yt-dlp, ~100 МБ)…'
    $zip = Join-Path $env:TEMP 'hlechyky-ffmpeg.zip'
    $tmp = Join-Path $env:TEMP 'hlechyky-ffmpeg'
    Invoke-WebRequest -UseBasicParsing 'https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip' -OutFile $zip
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip $tmp
    Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe | Copy-Item -Destination $yt
    Remove-Item $zip -Force; Remove-Item $tmp -Recurse -Force
}

# 2. Секрети: appsettings.Local.json і liquidsoap\.env. Ключ liquidsoap-callback має збігатися в обох файлах.
$local = Join-Path $Root 'appsettings.Local.json'
$envFile = Join-Path $Root 'liquidsoap\.env'
$liqKey = $null
if (Test-Path $local) {
    Write-Host 'appsettings.Local.json вже є'
    $liqKey = (Get-Content $local -Raw | ConvertFrom-Json).Liquidsoap.ApiKey
}
else {
    $liqKey = New-Key 24
    $admin = New-Key 18
    (Get-Content (Join-Path $Root 'appsettings.Local.example.json') -Raw) `
        -replace '<AdminKey>', $admin -replace '<LiquidsoapApiKey>', $liqKey |
        Set-Content $local -Encoding UTF8 -NoNewline
    Write-Host "Створив appsettings.Local.json. Адмінка: http://localhost:8080/?k=$admin"
}
if (Test-Path $envFile) { Write-Host 'liquidsoap\.env вже є' }
else {
    if (-not $liqKey) { $liqKey = New-Key 24; Write-Warning "У appsettings.Local.json порожній Liquidsoap:ApiKey; впиши туди $liqKey" }
    (Get-Content (Join-Path $Root 'liquidsoap\.env.example') -Raw) `
        -replace '<IcecastSourcePassword>', (New-Key 12) -replace '<LiquidsoapApiKey>', $liqKey |
        Set-Content $envFile -Encoding ASCII -NoNewline
    Write-Host 'Створив liquidsoap\.env'
}

# 3. Великий словник для Ерудита: data\words\uk-all.txt (~80 МБ, ~3.4 млн словоформ).
#    У гіті його нема — качаємо з релізу brown-uk/dict_uk (CC BY-NC-SA 4.0, див. data\words\LICENSE.txt).
#    Без нього сайт працює: Ерудит вмикає режим «малий словник», решта словесних ігор — як завжди.
$words = Join-Path $Root 'data\words'
New-Item -ItemType Directory -Force $words | Out-Null
$ukAll = Join-Path $words 'uk-all.txt'
$ukDb = Join-Path $words 'uk-all.db'
$dictUrl = 'https://github.com/brown-uk/dict_uk/releases/download/v6.8.5/dict_corp_vis.txt.bz2'
$dictSha = 'e33803783ac138e6f3af2cf0e9428ba146c0ecfda7f5c41fe83ae00c7af24be9'

function Find-Bzip2 {
    $c = Get-Command bzip2 -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $gitRoot = Split-Path (Split-Path $git.Source -Parent) -Parent   # ...\Git\cmd\git.exe -> ...\Git
        foreach ($rel in 'usr\bin\bzip2.exe', 'mingw64\bin\bzip2.exe') {
            $p = Join-Path $gitRoot $rel
            if (Test-Path $p) { return $p }
        }
    }
    return $null
}

if ((Test-Path $ukAll) -or (Test-Path $ukDb)) { Write-Host 'Великий словник уже є' }
else {
    $bz2 = Join-Path $env:TEMP 'hlechyky-dict-uk.txt.bz2'
    $raw = Join-Path $env:TEMP 'hlechyky-dict-uk.txt'
    try {
        Write-Host 'Качаю словник dict_uk (18 МБ)…'
        Invoke-WebRequest -UseBasicParsing $dictUrl -OutFile $bz2
        $sha = (Get-FileHash $bz2 -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sha -ne $dictSha) { throw "SHA256 не збігся: чекав $dictSha, отримав $sha" }

        $bzip2 = Find-Bzip2
        Write-Host 'Розпаковую (318 МБ на час обробки)…'
        # -dk кладе результат поруч, знявши .bz2 (hlechyky-dict-uk.txt.bz2 -> hlechyky-dict-uk.txt);
        # перенаправляти вивід через > не можна — PowerShell перекодував би текст
        if ($bzip2) { & $bzip2 -dk $bz2 }
        elseif (Get-Command python -ErrorAction SilentlyContinue) {
            python -c "import bz2,shutil,sys;shutil.copyfileobj(bz2.open(sys.argv[1],'rb'),open(sys.argv[2],'wb'))" $bz2 $raw
        }
        else { throw 'нема чим розпакувати .bz2 (шукав bzip2.exe і python)' }
        if (-not (Test-Path $raw)) { throw 'розпакування не дало файлу' }

        # Витягуємо словоформи: у dict_corp_vis рядок — «слово тег[ # коментар]», відступ означає похідну форму.
        # Беремо все, крім власних назв, абревіатур, лайки й латиниці; лишаємо тільки українські літери.
        # Цикл на 7 млн рядків у самому PowerShell тягнувся б хвилинами, тому маленький клас на C#.
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
public static class HlechykyWords {
    const string Alphabet = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";
    static readonly string[] Bad = { ":prop", ":abbr", ":bad", ":obsc", ":vulg", ":foreign", ":latin" };
    public static int Extract(string src, string dst) {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(src)) {
            var s = line.Trim();
            if (s.Length == 0) continue;
            var sp = s.IndexOf(' ');
            if (sp <= 0) continue;
            var word = s.Substring(0, sp);   // без ToLower: слово з великої — це власна назва, у Ерудиті їй не місце
            if (word.Length < 2) continue;
            var okWord = true;
            foreach (var ch in word) if (Alphabet.IndexOf(ch) < 0) { okWord = false; break; }
            if (!okWord) continue;
            var rest = s.Substring(sp + 1);
            var hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest.Substring(0, hash);
            var tag = ":" + rest.Trim() + ":";
            var okTag = true;
            foreach (var b in Bad) if (tag.Contains(b)) { okTag = false; break; }
            if (okTag) set.Add(word);
        }
        var all = new List<string>(set);
        all.Sort(StringComparer.Ordinal);
        using (var w = new StreamWriter(dst, false, new System.Text.UTF8Encoding(false))) {
            w.NewLine = "\n";
            foreach (var word in all) w.WriteLine(word);
        }
        return all.Count;
    }
}
'@
        Write-Host 'Складаю uk-all.txt…'
        $n = [HlechykyWords]::Extract($raw, $ukAll)
        Write-Host "Готово: $n словоформ у data\words\uk-all.txt (сервер збере з нього uk-all.db при першому старті)"
    }
    catch {
        Write-Warning "Великий словник не поставився: $_"
        Write-Warning 'Не біда: Ерудит гратиме в режимі «малий словник», решта ігор — без змін.'
        if (Test-Path $ukAll) { Remove-Item $ukAll -Force }
    }
    finally {
        foreach ($f in $bz2, $raw) { if (Test-Path $f) { Remove-Item $f -Force } }
    }
}

Write-Host ''
Write-Host 'Готово. Далі:'
Write-Host '  docker compose -f liquidsoap\docker-compose.dev.yml up -d   # Icecast + liquidsoap (необов''язково)'
Write-Host '  dotnet run --project src\Hlechyky                             # сайт на http://localhost:8080'
