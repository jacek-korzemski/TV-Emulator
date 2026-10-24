$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$builds = Get-Content (Join-Path $repo 'engines\builds.json') -Raw | ConvertFrom-Json
$downloads = Join-Path $repo 'engines\downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null

foreach ($build in $builds) {
    $dest = Join-Path $repo "engines\$($build.id)"
    $binary = if ($build.kind -eq 'electron') { 'electron.exe' } else { 'chrome.exe' }
    $existing = Get-ChildItem $dest -Recurse -Filter $binary -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) {
        Write-Output "READY $($build.id) $($existing.FullName)"
        continue
    }

    $zip = Join-Path $downloads "$($build.id).zip"
    if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 1000000) {
        Write-Output "GET $($build.id)"
        & curl.exe -L --fail --retry 3 --retry-delay 2 -o $zip $build.archive
        if ($LASTEXITCODE -ne 0) { throw "Pobieranie $($build.id) nie powiodło się." }
    }

    Write-Output "UNZIP $($build.id)"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    & tar.exe -xf $zip -C $dest
    if ($LASTEXITCODE -ne 0) { throw "Rozpakowanie $($build.id) nie powiodło się." }
}

Write-Output 'FETCH_DONE'
