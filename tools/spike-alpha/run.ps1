param([string]$Exe, [string]$Gpu = '1', [int]$Port = 9223, [string]$Page = 'page.html')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class SpikeAlphaWin5 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr hdc);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int n);
  public static string Describe(IntPtr hwnd) {
    var sb = new StringBuilder();
    EnumProc paint = null;
    paint = (h, l) => {
      var cls = new StringBuilder(80); GetClassName(h, cls, 80);
      RECT r; GetWindowRect(h, out r);
      int ex = GetWindowLong(h, -20);
      sb.Append(cls).Append(" ").Append(r.L).Append(",").Append(r.T).Append(" ").Append(r.R - r.L).Append("x").Append(r.B - r.T).Append(" ex=").Append(ex.ToString("x")).Append(" | ");
      return true;
    };
    paint(hwnd, IntPtr.Zero);
    EnumChildWindows(hwnd, paint, IntPtr.Zero);
    return sb.ToString();
  }
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int cx, int cy, IntPtr src, int sx, int sy, int rop);
  public static void Capture(IntPtr dest, int x, int y, int w, int h) {
    IntPtr screen = GetDC(IntPtr.Zero);
    BitBlt(dest, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
    ReleaseDC(IntPtr.Zero, screen);
  }
  public struct RECT { public int L, T, R, B; }
  public static IntPtr Top = new IntPtr(-1);
  public static string Find(int pid) {
    IntPtr found = IntPtr.Zero; int ww=0, hh=0, xx=0, yy=0;
    EnumWindows((hwnd, l) => {
      if (!IsWindowVisible(hwnd)) return true;
      uint p; GetWindowThreadProcessId(hwnd, out p);
      if ((int)p != pid) return true;
      var cls = new StringBuilder(80); GetClassName(hwnd, cls, 80);
      if (!cls.ToString().StartsWith("Chrome_WidgetWin")) return true;
      RECT r; if (!GetWindowRect(hwnd, out r)) return true;
      int rw = r.R - r.L, rh = r.B - r.T;
      if (rw * rh > ww * hh) { found = hwnd; ww = rw; hh = rh; xx = r.L; yy = r.T; }
      return true;
    }, IntPtr.Zero);
    if (found == IntPtr.Zero) return "";
    return found.ToInt64() + "," + xx + "," + yy + "," + ww + "," + hh;
  }
}
"@

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Test-Path (Join-Path $repo 'tools\spike-alpha\main.js'))) {
  $repo = Split-Path $PSScriptRoot -Parent
}
$app = Join-Path $repo 'tools\spike-alpha'
$page = ([Uri](Join-Path $app $Page)).AbsoluteUri
$data = Join-Path $env:TEMP ("tv-spike-alpha-" + [IO.Path]::GetFileName($Exe) + "-gpu" + $Gpu)
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory -Force -Path $data | Out-Null

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.BackColor = [System.Drawing.Color]::FromArgb(0, 255, 0)
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point 120, 80
$form.ClientSize = New-Object System.Drawing.Size 960, 540
$form.TopMost = $false
$form.ShowInTaskbar = $false
$form.Show()
for ($i = 0; $i -lt 10; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 30 }
$bounds = "$($form.Left),$($form.Top),$($form.Width),$($form.Height)"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = Split-Path $Exe -Parent
$psi.UseShellExecute = $false
$psi.Arguments = '"' + $app + '" --tv-url=' + $page + ' --tv-bounds=' + $bounds + ' --tv-gpu=' + $Gpu + ' --tv-user-data=' + $data + ' --tv-debug-port=' + $Port + ' --tv-scale=1'
$proc = [Diagnostics.Process]::Start($psi)
$hwnd = [IntPtr]::Zero
$w = 0; $h = 0; $x = 0; $y = 0
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
  [System.Windows.Forms.Application]::DoEvents()
  Start-Sleep -Milliseconds 200
  $proc.Refresh()
  $tree = @(Get-CimInstance Win32_Process -Filter "Name = 'electron.exe'" | Where-Object { $_.ExecutablePath -eq $Exe } | ForEach-Object { [int]$_.ProcessId })
  foreach ($procId in $tree) {
    $hit = [SpikeAlphaWin5]::Find($procId)
    if (-not $hit) { continue }
    $parts = $hit.Split(',')
    $candidate = [IntPtr][int64]$parts[0]
    $x = [int]$parts[1]; $y = [int]$parts[2]; $w = [int]$parts[3]; $h = [int]$parts[4]
    if ($w -gt 200 -and $h -gt 200) { $hwnd = $candidate }
  }
}
if ($hwnd -eq [IntPtr]::Zero) { throw "no electron window gpu=$Gpu" }
$form.Bounds = New-Object System.Drawing.Rectangle $x, $y, $w, $h
[SpikeAlphaWin5]::SetWindowPos($hwnd, [SpikeAlphaWin5]::Top, $x, $y, $w, $h, 0x0040) | Out-Null
Start-Sleep -Milliseconds 1500
for ($i = 0; $i -lt 5; $i++) { [System.Windows.Forms.Application]::DoEvents() }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[SpikeAlphaWin5]::Capture($hdc, $x, $y, $w, $h)
$g.ReleaseHdc($hdc)
$left = $bmp.GetPixel([int]($w * 0.25), [int]($h * 0.5))
$right = $bmp.GetPixel([int]($w * 0.75), [int]($h * 0.5))
$mark = $bmp.GetPixel([Math]::Max(0, $w - 8), 8)
$other = 0
for ($sy = 0; $sy -lt $h; $sy += 12) {
  for ($sx = 0; $sx -lt $w; $sx += 12) {
    $p = $bmp.GetPixel($sx, $sy)
    if (-not ($p.R -eq 0 -and $p.G -eq 255 -and $p.B -eq 0)) { $other++ }
  }
}
$bmp.Save((Join-Path $env:TEMP ("spike-alpha-" + $Gpu + ".png")))
$g.Dispose(); $bmp.Dispose()
if ($other -eq 0) {
  "windows $([SpikeAlphaWin5]::Describe($hwnd))"
  $form.Hide()
  Start-Sleep -Milliseconds 250
  $bmp2 = New-Object System.Drawing.Bitmap $w, $h
  $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
  $hdc2 = $g2.GetHdc()
  [SpikeAlphaWin5]::Capture($hdc2, $x, $y, $w, $h)
  $g2.ReleaseHdc($hdc2)
  $white = 0
  $p2 = $bmp2.GetPixel([Math]::Max(0, $w - 8), 8)
  for ($sy = 0; $sy -lt $h; $sy += 12) {
    for ($sx = 0; $sx -lt $w; $sx += 12) {
      $q = $bmp2.GetPixel($sx, $sy)
      if ($q.R -gt 240 -and $q.G -gt 240 -and $q.B -gt 240) { $white++ }
    }
  }
  "hidden-backdrop mark=$($p2.R),$($p2.G),$($p2.B) white=$white"
  $g2.Dispose(); $bmp2.Dispose()
}
$form.Close()

$fps = ''
try {
  $json = (Invoke-WebRequest -UseBasicParsing ("http://127.0.0.1:{0}/json" -f $Port)).Content
  $ws = ([regex]::Match($json, '"webSocketDebuggerUrl":\s*"(ws://[^"]+)"')).Groups[1].Value
  if ($ws) {
    $fps = node (Join-Path $app 'cdp.js') $ws "String(window.__fps)+','+typeof require+','+typeof process"
  }
} catch {
  $fps = "cdp-failed"
}

if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process -Filter "Name = 'electron.exe'" | Where-Object { $_.ExecutablePath -eq $Exe } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
"gpu=$Gpu left=$($left.R),$($left.G),$($left.B) right=$($right.R),$($right.G),$($right.B) mark=$($mark.R),$($mark.G),$($mark.B) other=$other rect=$x,$y ${w}x${h}"
"fps=$fps"
