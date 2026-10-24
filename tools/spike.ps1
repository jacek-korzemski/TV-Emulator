$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $PSScriptRoot 'probe'

Get-CimInstance Win32_Process -Filter "Name = 'chrome.exe'" |
    Where-Object { $_.ExecutablePath -like "$repo\engines\*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process -Filter "Name = 'electron.exe'" |
    Where-Object { $_.ExecutablePath -like "$repo\engines\*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SpikeWin {
  public static uint SWP_FRAMECHANGED = 0x0020;
  public static uint SWP_SHOWWINDOW = 0x0040;
  public static int GWL_STYLE = -16;
  public static int WS_CAPTION = 0x00C00000;
  public static int WS_THICKFRAME = 0x00040000;
  public static int WS_POPUP = unchecked((int)0x80000000);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int n, int v);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static void Strip(IntPtr hwnd, int x, int y, int w, int h) {
    int style = GetWindowLong(hwnd, GWL_STYLE);
    style &= ~(WS_CAPTION | WS_THICKFRAME | 0x00020000 | 0x00010000 | 0x00080000);
    style |= WS_POPUP;
    SetWindowLong(hwnd, GWL_STYLE, style);
    SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
  }
}
"@

function Quote-Arg([string]$value) {
    if ($value -match '[\s"]') { return '"' + ($value -replace '"', '\"') + '"' }
    return $value
}

function Get-ManifestVersion([string]$exe) {
    $dir = Split-Path $exe -Parent
    $best = $null
    Get-ChildItem $dir -Filter '*.manifest' | ForEach-Object {
        if ($_.BaseName -match '^\d+\.\d+\.\d+\.\d+$') {
            $version = [version]$_.BaseName
            if ($null -eq $best -or $version -gt $best) { $best = $version }
        }
    }
    if ($null -eq $best) { return '' }
    return $best.ToString()
}

function Start-Probe([int]$port) {
    $listener = New-Object System.Net.HttpListener
    $listener.Prefixes.Add("http://127.0.0.1:$port/")
    $listener.Start()
    return $listener
}

function Read-Body($request) {
    $reader = New-Object System.IO.StreamReader($request.InputStream, $request.ContentEncoding)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Write-Bytes($response, [int]$status, [string]$type, [byte[]]$bytes) {
    $response.StatusCode = $status
    $response.ContentType = $type
    $response.ContentLength64 = $bytes.Length
    if ($bytes.Length -gt 0) { $response.OutputStream.Write($bytes, 0, $bytes.Length) }
    $response.OutputStream.Close()
    $response.Close()
}

function Serve-Probe($context) {
    $path = $context.Request.Url.AbsolutePath
    if ($path -eq '/report' -and $context.Request.HttpMethod -eq 'POST') {
        $script:lastReport = Read-Body $context.Request
        Write-Bytes $context.Response 204 'text/plain' ([byte[]]@())
        return
    }
    $relative = if ($path -eq '/') { 'index.html' } else { $path.TrimStart('/') }
    $file = Join-Path $probe ($relative -replace '/', '\')
    $rootFull = [System.IO.Path]::GetFullPath($probe)
    $full = [System.IO.Path]::GetFullPath($file)
    if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path $full -PathType Leaf)) {
        $missing = [System.Text.Encoding]::UTF8.GetBytes('missing')
        Write-Bytes $context.Response 404 'text/plain' $missing
        return
    }
    if ($relative -eq 'index.html') {
        $html = [System.IO.File]::ReadAllText($full)
        $html = $html -replace '<head>', '<head><script>window.__tvSpikeInjected=true;</script>'
        Write-Bytes $context.Response 200 'text/html; charset=utf-8' ([System.Text.Encoding]::UTF8.GetBytes($html))
        return
    }
    Write-Bytes $context.Response 200 'text/javascript; charset=utf-8' ([System.IO.File]::ReadAllBytes($full))
}

function Wait-Report($listener, [int]$seconds) {
    $script:lastReport = $null
    $deadline = (Get-Date).AddSeconds($seconds)
    $async = $listener.BeginGetContext($null, $null)
    while ((Get-Date) -lt $deadline) {
        if (-not $async.AsyncWaitHandle.WaitOne(400)) { continue }
        $context = $listener.EndGetContext($async)
        try { Serve-Probe $context } catch { try { $context.Response.Abort() } catch {} }
        if ($script:lastReport) {
            $parsed = $script:lastReport | ConvertFrom-Json
            if ($null -ne $parsed.letParsed) { return $parsed }
        }
        $async = $listener.BeginGetContext($null, $null)
    }
    return $null
}

function Stop-Engine($process) {
    if ($null -eq $process) { return }
    & taskkill.exe /PID $process.Id /T /F | Out-Null
}

$engines = @(
    @{ Id = 'chromium-47'; Let = $false; Class = $false; Async = $false; Grid = $false; Flex = $false },
    @{ Id = 'chromium-53'; Let = $true; Class = $true; Async = $false; Grid = $false; Flex = $false },
    @{ Id = 'chromium-56'; Let = $true; Class = $true; Async = $true; Grid = $false; Flex = $false },
    @{ Id = 'chromium-63'; Let = $true; Class = $true; Async = $true; Grid = $true; Flex = $false },
    @{ Id = 'chromium-85'; Let = $true; Class = $true; Async = $true; Grid = $true; Flex = $true }
)
$attempts = @(
    @{ Flags = @('--no-sandbox', '--disable-gpu', '--no-proxy-server') }
)
$results = @()
$port = 47921

foreach ($engine in $engines) {
    $exe = Get-ChildItem (Join-Path $repo "engines\$($engine.Id)") -Recurse -Filter chrome.exe | Select-Object -First 1
    if (-not $exe) { throw "Brak chrome.exe dla $($engine.Id)" }
    $version = Get-ManifestVersion $exe.FullName
    $winner = $false
    foreach ($attempt in $attempts) {
        $extra = @($attempt.Flags)
        $listener = Start-Probe $port
        $data = Join-Path $env:TEMP ("tv-emulator-spike\" + $engine.Id + "-" + $extra.Count)
        if (Test-Path $data) { Remove-Item $data -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        '' | Set-Content -Path (Join-Path $data 'First Run')
        $chromeArgs = @(
            "--user-data-dir=$data",
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-sync',
            '--disable-extensions',
            '--disable-translate',
            '--disable-background-networking',
            '--noerrdialogs',
            '--force-device-scale-factor=1',
            '--window-position=80,60',
            '--window-size=1280,720',
            '--user-agent=TvEmulatorSpike'
        ) + $extra + @("--app=http://127.0.0.1:$port/")
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe.FullName
        $psi.WorkingDirectory = $exe.DirectoryName
        $psi.UseShellExecute = $false
        $psi.Arguments = (($chromeArgs | ForEach-Object { Quote-Arg $_ }) -join ' ')
        Write-Output ("ARGS " + $psi.Arguments)
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        [void]$process.Start()
        $report = Wait-Report $listener 25
        $window = @{ found = $false; captionBefore = $null; captionAfter = $null; width = 0; height = 0; keyCode = $null }
        if ($report) {
            $deadline = (Get-Date).AddSeconds(8)
            $hwnd = [IntPtr]::Zero
            while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
                $process.Refresh()
                $hwnd = $process.MainWindowHandle
                if ($hwnd -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 200 }
            }
            if ($hwnd -ne [IntPtr]::Zero) {
                $window.found = $true
                $style = [SpikeWin]::GetWindowLong($hwnd, [SpikeWin]::GWL_STYLE)
                $window.captionBefore = (($style -band [SpikeWin]::WS_CAPTION) -ne 0)
                [SpikeWin]::Strip($hwnd, 80, 60, 1280, 720)
                $style = [SpikeWin]::GetWindowLong($hwnd, [SpikeWin]::GWL_STYLE)
                $window.captionAfter = (($style -band [SpikeWin]::WS_CAPTION) -ne 0)
                $rect = New-Object SpikeWin+RECT
                [void][SpikeWin]::GetWindowRect($hwnd, [ref]$rect)
                $window.width = $rect.Right - $rect.Left
                $window.height = $rect.Bottom - $rect.Top
                [void][SpikeWin]::SetForegroundWindow($hwnd)
                Start-Sleep -Milliseconds 300
                [SpikeWin]::keybd_event(0x41, 0, 0, [UIntPtr]::Zero)
                [SpikeWin]::keybd_event(0x41, 0, 2, [UIntPtr]::Zero)
                $keyed = Wait-Report $listener 6
                if ($keyed -and $null -ne $keyed.keyCode) { $window.keyCode = $keyed.keyCode }
            }
        }
        Stop-Engine $process
        $listener.Stop()
        $listener.Close()
        $pass = $false
        if ($report) {
            $pass = ($report.letParsed -eq $engine.Let) -and ($report.classParsed -eq $engine.Class) -and ($report.asyncParsed -eq $engine.Async) -and ($report.grid -eq $engine.Grid) -and ($report.flexGap -eq $engine.Flex) -and ($report.injected -eq $true) -and ($report.gamepadApi -eq $true) -and ($window.found -eq $true) -and ($window.captionAfter -eq $false)
        }
        $row = [ordered]@{
            id = $engine.Id
            version = $version
            args = @($extra)
            pass = $pass
            report = $report
            window = $window
        }
        $results += [pscustomobject]$row
        Write-Output ("RESULT {0} flags=[{1}] pass={2} let={3} class={4} async={5} grid={6} flexGap={7} injected={8} gamepad={9} window={10} captionAfter={11} key={12}" -f `
            $engine.Id, ($extra -join ' '), $pass, $report.letParsed, $report.classParsed, $report.asyncParsed, $report.grid, $report.flexGap, $report.injected, $report.gamepadApi, $window.found, $window.captionAfter, $window.keyCode)
        if ($pass) { $winner = $true; break }
        $port += 1
    }
    $port += 1
    if (-not $winner) { Write-Output "FAIL $($engine.Id)" }
}

$out = Join-Path $repo 'engines\spike-results.json'
$results | ConvertTo-Json -Depth 6 | Set-Content -Path $out -Encoding UTF8
Write-Output "WROTE $out"
foreach ($engineId in @('chromium-47', 'chromium-53', 'chromium-56', 'chromium-63', 'chromium-85')) {
    $ok = @($results | Where-Object { $_.id -eq $engineId -and $_.pass })
    if ($ok.Count -lt 1) { exit 1 }
}

$shell = Join-Path $repo 'shell\electron'
$electronEngines = @(
    @{ Id = 'electron-53'; Let = $true; Class = $true; Async = $false; Grid = $false; Flex = $false },
    @{ Id = 'electron-56'; Let = $true; Class = $true; Async = $true; Grid = $false; Flex = $false },
    @{ Id = 'electron-61'; Let = $true; Class = $true; Async = $true; Grid = $true; Flex = $false },
    @{ Id = 'electron-85'; Let = $true; Class = $true; Async = $true; Grid = $true; Flex = $true }
)
foreach ($engine in $electronEngines) {
    $exe = Get-ChildItem (Join-Path $repo "engines\$($engine.Id)") -Recurse -Filter electron.exe | Select-Object -First 1
    if (-not $exe) { throw "Brak electron.exe dla $($engine.Id)" }
    $listener = Start-Probe $port
    $data = Join-Path $env:TEMP ("tv-emulator-spike\" + $engine.Id)
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    $electronArgs = @(
        $shell,
        '--tv-user-agent=TvEmulatorSpike',
        '--tv-bounds=80,60,1280,720',
        '--tv-scale=1',
        "--tv-user-data=$data",
        "--tv-url=http://127.0.0.1:$port/"
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe.FullName
    $psi.WorkingDirectory = $exe.DirectoryName
    $psi.UseShellExecute = $false
    $psi.Arguments = (($electronArgs | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    [void]$process.Start()
    $report = Wait-Report $listener 25
    Stop-Engine $process
    $listener.Stop()
    $listener.Close()
    if (-not $report) { Write-Output "ELECTRON $($engine.Id) no report"; exit 1 }
    $pass = $false
    if ($report) {
        $pass = ($report.letParsed -eq $engine.Let) -and ($report.classParsed -eq $engine.Class) -and ($report.asyncParsed -eq $engine.Async) -and ($report.grid -eq $engine.Grid) -and ($report.flexGap -eq $engine.Flex) -and ($report.injected -eq $true) -and ($report.hasRequire -eq $false) -and ($report.hasProcess -eq $false) -and ($report.userAgent -eq 'TvEmulatorSpike')
    }
    Write-Output ("ELECTRON {0} pass={1} let={2} class={3} async={4} grid={5} flexGap={6} ua={7} worker={8} require={9} chrome={10}" -f `
        $engine.Id, $pass, $report.letParsed, $report.classParsed, $report.asyncParsed, $report.grid, $report.flexGap, $report.userAgent, $report.workerUa, $report.hasRequire, $report.chromeObject)
    if (-not $pass) { exit 1 }
    $port += 1
}

Write-Output 'SPIKE_OK'
