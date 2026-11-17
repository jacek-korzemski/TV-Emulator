param(
    [string]$QtRoot = $env:QTROOT,
    [string]$Zip
)

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$here = $PSScriptRoot
$stage = Join-Path $here 'stage'
$qtInstall = Join-Path $env:LOCALAPPDATA 'Qt\Qt5.5.1'
$opensslZip = 'https://github.com/IndySockets/OpenSSL-Binaries/raw/master/openssl-1.0.2u-i386-win32.zip'
$opensslMirror = 'https://indy.fulgan.com/SSL/openssl-1.0.2u-i386-win32.zip'
$qtInstaller = 'https://download.qt.io/new_archive/qt/5.5/5.5.1/qt-opensource-windows-x86-mingw492-5.5.1.exe'

function Find-Qmake([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path $root)) {
        return $null
    }
    $direct = Join-Path $root 'bin\qmake.exe'
    if (Test-Path $direct) { return (Resolve-Path $direct).Path }
    $found = Get-ChildItem $root -Recurse -Filter qmake.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'mingw492' } |
        Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

function Add-Mingw([string]$qmake) {
    $qtBin = Split-Path $qmake -Parent
    $qtBase = Split-Path (Split-Path $qtBin -Parent) -Parent
    $candidates = @(
        (Join-Path $qtBase 'Tools\mingw492_32\bin'),
        (Join-Path (Split-Path $qtBase -Parent) 'Tools\mingw492_32\bin'),
        'C:\Qt\Qt5.5.1\Tools\mingw492_32\bin',
        'C:\Qt\Tools\mingw492_32\bin',
        (Join-Path $qtInstall 'Tools\mingw492_32\bin')
    )
    foreach ($bin in $candidates) {
        if (Test-Path (Join-Path $bin 'g++.exe')) {
            $env:PATH = "$bin;$qtBin;$env:PATH"
            return
        }
    }
    $env:PATH = "$qtBin;$env:PATH"
    if (-not (Get-Command g++.exe -ErrorAction SilentlyContinue)) {
        throw "Nie ma g++.exe obok Qt ($qmake). Ustaw QTROOT na katalog 5.5.1 mingw492_32."
    }
}

function Install-Qt551 {
    $existing = Find-Qmake $qtInstall
    if ($existing) { return $existing }
    $payload = Join-Path $here 'qt-opensource-windows-x86-mingw492-5.5.1.exe'
    if (-not (Test-Path $payload) -or (Get-Item $payload).Length -lt 1000000000) {
        Write-Host "GET Qt 5.5.1"
        & curl.exe -L --fail --retry 3 --retry-delay 2 -o $payload $qtInstaller
        if ($LASTEXITCODE -ne 0) { throw "Pobieranie Qt 5.5.1 nie powiodło się." }
    }
    $script = Join-Path $here 'qt-install.qs'
    $target = $qtInstall -replace '\\', '/'
    Write-Host "INSTALL Qt 5.5.1 -> $target"
    if (Test-Path $qtInstall) {
        Write-Host "REMOVE incomplete $qtInstall"
        Remove-Item $qtInstall -Recurse -Force
    }
    $env:TargetDir = $target
    & $payload --script $script "TargetDir=$target" | Out-Null
    $deadline = (Get-Date).AddMinutes(45)
    do {
        $running = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -like 'qt-opensource-windows-x86-mingw492*' }
        if (-not $running) { break }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    if ($LASTEXITCODE -ne 0 -and -not (Find-Qmake $qtInstall)) {
        Write-Host "Installer zakończył się kodem $LASTEXITCODE. Szukam qmake mimo to."
    }
    $qmake = Find-Qmake $qtInstall
    if (-not $qmake) { throw "Instalator Qt nie zostawił qmake.exe w $qtInstall." }
    return $qmake
}

function Try-Aqt {
    if (Find-Qmake 'C:\Qt') {
        return Find-Qmake 'C:\Qt'
    }
    try {
        Write-Host "TRY aqtinstall"
        & python -m pip install --disable-pip-version-check aqtinstall
        if ($LASTEXITCODE -ne 0) { return $null }
        & python -m aqt install-qt windows desktop 5.5.1 win32_mingw492 -O C:\Qt -m qtwebkit
        if ($LASTEXITCODE -ne 0) {
            & python -m aqt install-qt windows desktop 5.5.1 win32_mingw --archives qtbase qtwebkit qttools icu -O C:\Qt
        }
    } catch {
        Write-Host "aqtinstall unavailable"
        return $null
    }
    return Find-Qmake 'C:\Qt'
}

if ([string]::IsNullOrWhiteSpace($QtRoot)) {
    $QtRoot = $qtInstall
}

$qmake = Find-Qmake $QtRoot
if (-not $qmake) {
    # The 5.5.1 online repository is gone, so aqtinstall cannot fetch this Qt.
    $qmake = Install-Qt551
}
if (-not $qmake) { throw "Brak qmake.exe dla Qt 5.5.1 MinGW." }

Write-Output "QMAKE $qmake"
Add-Mingw $qmake

if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Push-Location $stage
try {
    Write-Output 'QMAKE'
    & $qmake (Join-Path $here 'webkit-host.pro') -spec win32-g++ "CONFIG+=release"
    if ($LASTEXITCODE -ne 0) { throw "qmake nie powiódł się." }
    Write-Output 'MAKE'
    & mingw32-make -j4
    if ($LASTEXITCODE -ne 0) { throw "mingw32-make nie powiódł się." }
}
finally {
    Pop-Location
}

$built = Join-Path $stage 'release\tv-webkit.exe'
if (-not (Test-Path $built)) {
    $built = Get-ChildItem $stage -Recurse -Filter tv-webkit.exe | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $built -or -not (Test-Path $built)) { throw "Brak tv-webkit.exe po kompilacji." }

$deploy = Join-Path $here 'dist'
if (Test-Path $deploy) {
    Remove-Item $deploy -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $deploy | Out-Null
Copy-Item $built (Join-Path $deploy 'tv-webkit.exe')

$windeploy = Join-Path (Split-Path $qmake -Parent) 'windeployqt.exe'
if (-not (Test-Path $windeploy)) { throw "Brak windeployqt.exe obok qmake." }
Write-Output 'DEPLOY'
& $windeploy --release --no-translations --no-angle (Join-Path $deploy 'tv-webkit.exe')
if ($LASTEXITCODE -ne 0) { throw "windeployqt nie powiódł się." }

$opensslPayload = Join-Path $here 'openssl-1.0.2u-i386-win32.zip'
if (-not (Test-Path $opensslPayload) -or (Get-Item $opensslPayload).Length -lt 100000) {
    Write-Output 'GET OpenSSL 1.0.2u'
    & curl.exe -L --fail --retry 3 --retry-delay 2 -o $opensslPayload $opensslZip
    if ($LASTEXITCODE -ne 0) {
        & curl.exe -L --fail --retry 3 --retry-delay 2 -o $opensslPayload $opensslMirror
    }
    if ($LASTEXITCODE -ne 0) { throw "Pobieranie OpenSSL 1.0.2u nie powiodło się." }
}
$opensslDir = Join-Path $here 'openssl'
if (Test-Path $opensslDir) { Remove-Item $opensslDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $opensslDir | Out-Null
& tar.exe -xf $opensslPayload -C $opensslDir
if ($LASTEXITCODE -ne 0) { throw "Rozpakowanie OpenSSL nie powiodło się." }
foreach ($dll in @('libeay32.dll', 'ssleay32.dll')) {
    $file = Get-ChildItem $opensslDir -Recurse -Filter $dll | Select-Object -First 1
    if (-not $file) { throw "W paczce OpenSSL nie ma $dll." }
    Copy-Item $file.FullName (Join-Path $deploy $dll)
}

if ($Zip) {
    $zipPath = $Zip
    if (-not [System.IO.Path]::IsPathRooted($zipPath)) {
        $zipPath = Join-Path (Split-Path $here -Parent | Split-Path -Parent) $Zip
    }
    $zipDir = Split-Path $zipPath -Parent
    if ($zipDir -and -not (Test-Path $zipDir)) {
        New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
    }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Output "ZIP $zipPath"
    & tar.exe -a -cf $zipPath -C $deploy *
    if ($LASTEXITCODE -ne 0) { throw "Pakowanie zip nie powiodło się." }
}

Write-Output "WEBKIT_HOST_DONE $deploy"
