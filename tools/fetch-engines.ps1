$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$builds = Get-Content (Join-Path $repo 'engines\builds.json') -Raw | ConvertFrom-Json
$downloads = Join-Path $repo 'engines\downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null

if (-not ('TvEmulator.SevenZipSig' -as [type])) {
    Add-Type -TypeDefinition @'
using System.IO;
public static class TvEmulatorSevenZipSig {
    public static int Find(string path) {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < bytes.Length - 6; i++) {
            if (bytes[i] == 0x37 && bytes[i + 1] == 0x7A && bytes[i + 2] == 0xBC
                && bytes[i + 3] == 0xAF && bytes[i + 4] == 0x27 && bytes[i + 5] == 0x1C)
                return i;
        }
        return -1;
    }
}
'@
}

function Expand-SevenZipPayload($payload, $dest) {
    $offset = [TvEmulatorSevenZipSig]::Find($payload)
    if ($offset -lt 0) { throw "W $($payload) nie ma archiwum 7z." }

    $archive = [System.IO.Path]::ChangeExtension($payload, '.7z')
    $inputStream = [System.IO.File]::OpenRead($payload)
    try {
        $inputStream.Position = $offset
        $output = [System.IO.File]::Create($archive)
        try { $inputStream.CopyTo($output) } finally { $output.Dispose() }
    } finally { $inputStream.Dispose() }
    $seven = Join-Path $downloads '7zr.exe'
    if (-not (Test-Path $seven) -or (Get-Item $seven).Length -lt 100000) {
        Write-Output "GET 7zr"
        & curl.exe -L --fail --retry 3 --retry-delay 2 -o $seven 'https://www.7-zip.org/a/7zr.exe'
        if ($LASTEXITCODE -ne 0) { throw "Pobieranie 7zr.exe nie powiodło się." }
    }
    & $seven x -y "-o$dest" $archive
    if ($LASTEXITCODE -ne 0) { throw "Rozpakowanie $archive nie powiodło się." }
}

foreach ($build in $builds) {
    $dest = Join-Path $repo "engines\$($build.id)"
    $binary = if ($build.kind -eq 'electron') { 'electron.exe' }
        elseif ($build.kind -eq 'firefox') { 'firefox.exe' }
        else { 'chrome.exe' }
    $existing = Get-ChildItem $dest -Recurse -Filter $binary -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) {
        Write-Output "READY $($build.id) $($existing.FullName)"
        continue
    }

    $isExe = $build.archive -match '\.exe$'
    $payload = Join-Path $downloads ($(if ($isExe) { "$($build.id).exe" } else { "$($build.id).zip" }))
    if (-not (Test-Path $payload) -or (Get-Item $payload).Length -lt 1000000) {
        Write-Output "GET $($build.id)"
        & curl.exe -L --fail --retry 3 --retry-delay 2 -o $payload $build.archive
        if ($LASTEXITCODE -ne 0) { throw "Pobieranie $($build.id) nie powiodło się." }
    }

    Write-Output "UNZIP $($build.id)"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    if ($isExe) {
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Expand-SevenZipPayload $payload $dest
    } else {
        & tar.exe -xf $payload -C $dest
        if ($LASTEXITCODE -ne 0) { throw "Rozpakowanie $($build.id) nie powiodło się." }
    }
}

Write-Output 'FETCH_DONE'
