param(
    [switch]$SkipEngines
)

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\TvEmulator\TvEmulator.csproj'
$stage = Join-Path $repo 'dist\publish-stage'
$dest = Join-Path $repo 'dist\TvEmulator-portable'

function Copy-Tree([string]$source, [string]$target) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    & robocopy.exe $source $target /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Kopiowanie $source nie powiodło się (robocopy $LASTEXITCODE)."
    }
}

if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}

Write-Output 'PUBLISH'
& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "Publikacja nie powiodła się." }

$exe = Join-Path $stage 'TvEmulator.exe'
if (-not (Test-Path $exe)) { throw "Brak $exe po publikacji." }

if (Test-Path $dest) {
    Remove-Item $dest -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $exe (Join-Path $dest 'TvEmulator.exe')

foreach ($name in @('profiles', 'shims', 'debug-ui', 'samples')) {
    $source = Join-Path $repo $name
    if (-not (Test-Path $source)) { throw "Brak katalogu $source." }
    Copy-Tree $source (Join-Path $dest $name)
}

$enginesDest = Join-Path $dest 'engines'
New-Item -ItemType Directory -Force -Path $enginesDest | Out-Null
Copy-Item (Join-Path $repo 'engines\builds.json') (Join-Path $enginesDest 'builds.json')

if (-not $SkipEngines) {
    $found = @()
    foreach ($pattern in @('chromium-*', 'chrome-*', 'firefox-*')) {
        $found += @(Get-ChildItem (Join-Path $repo 'engines') -Directory -Filter $pattern -ErrorAction SilentlyContinue)
    }
    if ($found.Count -eq 0) {
        Write-Output 'NO_ENGINES engines\builds.json jest w paczce, ale nie znaleziono rozpakowanych przeglądarek. Uruchom tools\fetch-engines.ps1 i złóż paczkę ponownie.'
    }
    foreach ($dir in $found) {
        Write-Output "COPY $($dir.Name)"
        Copy-Tree $dir.FullName (Join-Path $enginesDest $dir.Name)
    }
}

Remove-Item $stage -Recurse -Force
Write-Output "PORTABLE_DONE $dest"
