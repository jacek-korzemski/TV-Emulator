$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class SessWin5 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int n);
  [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("kernel32.dll")] public static extern int GetLastError();
  public static readonly IntPtr TopMost = new IntPtr(-1);
  public struct RECT { public int L, T, R, B; }
  public static string List() {
    var sb = new StringBuilder();
    EnumWindows((hwnd, l) => {
      if (!IsWindowVisible(hwnd)) return true;
      uint pid; GetWindowThreadProcessId(hwnd, out pid);
      var cls = new StringBuilder(80); GetClassName(hwnd, cls, 80);
      if (!cls.ToString().StartsWith("Chrome_WidgetWin")) return true;
      RECT r; if (!GetWindowRect(hwnd, out r)) return true;
      int rw = r.R - r.L, rh = r.B - r.T;
      if (rw < 400 || rh < 300) return true;
      int style = GetWindowLong(hwnd, -16);
      if (sb.Length > 0) sb.Append("|");
      sb.Append(hwnd.ToInt64()).Append(",").Append((int)pid).Append(",").Append(r.L).Append(",").Append(r.T).Append(",").Append(rw).Append(",").Append(rh).Append(",").Append(style.ToString("x"));
      return true;
    }, IntPtr.Zero);
    return sb.ToString();
  }
}
"@

function Get-Lumas([int]$x, [int]$y, [int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  $screen = [SessWin5]::GetDC([IntPtr]::Zero)
  [SessWin5]::BitBlt($hdc, 0, 0, $w, $h, $screen, $x, $y, 0x40CC0020) | Out-Null
  [SessWin5]::ReleaseDC([IntPtr]::Zero, $screen) | Out-Null
  $g.ReleaseHdc($hdc)
  $cx = [int]($w / 2); $cy = [int]($h * 0.35); $by = [int]($h * 0.9)
  $c = $bmp.GetPixel($cx, $cy)
  $b = $bmp.GetPixel($cx, $by)
  $bmp.Save("$env:TEMP\sess-alpha.png")
  $g.Dispose(); $bmp.Dispose()
  return "$($c.R),$($c.G),$($c.B)|$($b.R),$($b.G),$($b.B)"
}

function Select-Profile($win, $procId, $label) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $box = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ProfileBox")))
  $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Start-Sleep -Milliseconds 400
  $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  $target = $null
  foreach ($el in $items) {
    if ($el.Current.ProcessId -ne $procId) { continue }
    $supports = $false
    foreach ($p in $el.GetSupportedPatterns()) { if ($p.ProgrammaticName -eq "SelectionItemPatternIdentifiers.Pattern") { $supports = $true } }
    if (-not $supports) { continue }
    $texts = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -eq $label) { $target = $el } }
  }
  if (-not $target) { throw "no profile $label" }
  $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 200
}

function Start-One([string]$profileFile, [string]$sample) {
  $repo = "G:\projekty\TV-Emulator"
  $meta = Get-Content (Join-Path $repo "profiles\$profileFile") -Raw -Encoding UTF8 | ConvertFrom-Json
  $exe = Join-Path $repo "src\TvEmulator\bin\Debug\net8.0-windows\TvEmulator.exe"
  Start-Process -FilePath $exe -WorkingDirectory $repo
  Start-Sleep -Seconds 1
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $win = $null
  for ($i = 0; $i -lt 25 -and -not $win; $i++) {
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "TV Emulator")))
    if (-not $win) { Start-Sleep -Milliseconds 200 }
  }
  if (-not $win) { throw "no emulator window" }
  $procId = $win.Current.ProcessId
  Select-Profile $win $procId $meta.label
  $dir = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "DirectoryBox")))
  $dir.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $repo $sample))
  $dbg = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "DebugBox")))
  if ($dbg.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne "On") { $dbg.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
  $start = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "StartButton")))
  $before = (Get-Date).AddSeconds(-2)
  $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $log = $null
  $deadline = (Get-Date).AddSeconds(25)
  while ((Get-Date) -lt $deadline) {
    $log = Get-ChildItem (Join-Path $repo "logs\session-*.log") | Where-Object { $_.LastWriteTime -gt $before } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log -and (Select-String -Path $log.FullName -Pattern "start profile=" -Quiet)) {
      if ($sample -notlike "*avplay*") { break }
      if (Select-String -Path $log.FullName -Pattern "sample viewport" -Quiet) { break }
    }
    Start-Sleep -Milliseconds 400
  }
  Start-Sleep -Seconds 6
  $rect = ""
  foreach ($item in ([SessWin5]::List() -split '\|')) {
    if (-not $item) { continue }
    $parts = $item.Split(',')
    $owner = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $parts[1])
    if ($owner.ExecutablePath -like '*\engines\*') { $rect = $item }
  }
  $colors = ""
  if ($rect) {
    $p = $rect.Split(",")
    $colors = Get-Lumas ([int]$p[2]) ([int]$p[3]) ([int]$p[4]) ([int]$p[5])
  }
  $hole = 0; $shade = 0; $view = ""
  if ($log) {
    $hole = @(Select-String -Path $log.FullName -Pattern "avplay-hole ").Count
    $shade = @(Select-String -Path $log.FullName -Pattern "avplay-shade").Count
    $view = (Select-String -Path $log.FullName -Pattern "sample viewport" | Select-Object -Last 1).Line
  }
  $fps = ""
  try {
    $json = (Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:9223/json" -TimeoutSec 3).Content
    $ws = ([regex]::Match($json, '"webSocketDebuggerUrl":\s*"(ws://[^"]+)"')).Groups[1].Value
    if ($ws) {
      $fps = node (Join-Path $repo "tools\spike-alpha\cdp.js") $ws "window.innerWidth+'x'+window.innerHeight"
    }
  } catch { $fps = "cdp-failed" }
  Get-Process -Id $procId -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
  $wait = (Get-Date).AddSeconds(12)
  while ((Get-Process -Id $procId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 300 }
  if (Get-Process -Id $procId -ErrorAction SilentlyContinue) { Stop-Process -Id $procId -Force }
  "{0} rect={1} colors={2} hole={3} shade={4} view={5} fps={6}" -f $meta.id, $rect, $colors, $hole, $shade, $view, $fps
}

Get-Process TvEmulator -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Seconds 1
Start-One "tizen-2022.json" "samples\tizen-avplay"
