# uia-dump.ps1 — vuelca el arbol de accesibilidad (UI Automation) de las ventanas de Teams.
# SOLO LECTURA: no clickea, no roba foco, no cierra nada. Correr con Windows PowerShell 5.1:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File uia-dump.ps1 [-SoloTitulo 'reuni'] [-Raw]
param(
    [string]$SoloTitulo = '',        # regex sobre el titulo de la ventana; vacio = todas las visibles
    [switch]$Raw,                    # usar RawView (incluye nodos no-control) en vez de ControlView
    [switch]$IncluirOcultas,         # volcar tambien ventanas no visibles (Teams "cerrado" a la bandeja)
    [int]$MaxDepth = 60,
    [string]$OutDir = "$PSScriptRoot\out"
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class WinEnum {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public class Win { public IntPtr H; public string Title; public string Class; public bool Visible; public int X, Y, W, Hh; public int Cloaked; public uint Pid; }
  public static List<Win> List(uint[] pids) {
    var res = new List<Win>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (Array.IndexOf(pids, p) < 0) return true;
      var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
      var cb = new StringBuilder(256); GetClassName(h, cb, 256);
      RECT r; GetWindowRect(h, out r); int cl = 0; DwmGetWindowAttribute(h, 14, out cl, 4);
      var w = new Win(); w.H = h; w.Title = sb.ToString(); w.Class = cb.ToString(); w.Visible = IsWindowVisible(h);
      w.X = r.L; w.Y = r.T; w.W = r.R - r.L; w.Hh = r.B - r.T; w.Cloaked = cl; w.Pid = p; res.Add(w); return true;
    }, IntPtr.Zero);
    return res;
  }
}
"@
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$pids = @(Get-Process ms-teams -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
if ($pids.Count -eq 0) { Write-Host 'ms-teams.exe no esta corriendo'; exit 1 }
$wins = [WinEnum]::List([uint32[]]$pids)
Write-Host ("Ventanas top-level de ms-teams ({0} procesos): {1}" -f $pids.Count, $wins.Count)
foreach ($w in $wins) {
    if (-not $w.Visible -and $w.Title -eq '') { continue }
    Write-Host ("  pid={0,-7} hwnd={1,-8} vis={2,-5} cloak={3} {4}x{5}@{6},{7} class='{8}' title='{9}'" -f $w.Pid, $w.H, $w.Visible, $w.Cloaked, $w.W, $w.Hh, $w.X, $w.Y, $w.Class, $w.Title)
}

$A = [System.Windows.Automation.AutomationElement]
$cr = New-Object System.Windows.Automation.CacheRequest
foreach ($p in @($A::NameProperty, $A::ControlTypeProperty, $A::AutomationIdProperty, $A::ClassNameProperty,
                 $A::LocalizedControlTypeProperty, $A::IsOffscreenProperty, $A::BoundingRectangleProperty,
                 $A::IsEnabledProperty, $A::HelpTextProperty, $A::IsInvokePatternAvailableProperty,
                 $A::IsTogglePatternAvailableProperty, $A::IsExpandCollapsePatternAvailableProperty,
                 $A::IsSelectionItemPatternAvailableProperty, $A::IsValuePatternAvailableProperty,
                 [System.Windows.Automation.ValuePattern]::ValueProperty,
                 [System.Windows.Automation.TogglePattern]::ToggleStateProperty)) { $cr.Add($p) }
$cr.TreeScope = [System.Windows.Automation.TreeScope]::Element -bor [System.Windows.Automation.TreeScope]::Descendants
if ($Raw) { $cr.TreeFilter = [System.Windows.Automation.Automation]::RawViewCondition } else { $cr.TreeFilter = [System.Windows.Automation.Automation]::ControlViewCondition }
$cr.AutomationElementMode = [System.Windows.Automation.AutomationElementMode]::None   # solo cache, sin referencias vivas = mas rapido

$interes = 'Personas|People|Particip|Salir|Leave|Colgar|Hang|reuni|meeting|Esperando|Waiting|nico|only|Vista compacta|llamada|call'
$script:count = 0
function Vuelca($el, $depth, $sb, $hits) {
    if ($depth -gt $MaxDepth) { return }
    $script:count++
    $ct = $el.Cached.ControlType.ProgrammaticName -replace '^ControlType\.', ''
    $name = $el.Cached.Name; $aid = $el.Cached.AutomationId; $cls = $el.Cached.ClassName
    $r = $el.Cached.BoundingRectangle
    $rs = 'sin-rect'
    if (-not ([double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Width) -or $r.IsEmpty)) { $rs = ('{0},{1} {2}x{3}' -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height) }
    $flags = ''
    if ($el.Cached.IsOffscreen) { $flags += ' off' }
    if (-not $el.Cached.IsEnabled) { $flags += ' disabled' }
    try { if ($el.GetCachedPropertyValue($A::IsInvokePatternAvailableProperty)) { $flags += ' [Invoke]' } } catch {}
    try { if ($el.GetCachedPropertyValue($A::IsTogglePatternAvailableProperty)) { $flags += ' [Toggle=' + $el.GetCachedPropertyValue([System.Windows.Automation.TogglePattern]::ToggleStateProperty) + ']' } } catch {}
    try { if ($el.GetCachedPropertyValue($A::IsExpandCollapsePatternAvailableProperty)) { $flags += ' [Expand]' } } catch {}
    try { if ($el.GetCachedPropertyValue($A::IsSelectionItemPatternAvailableProperty)) { $flags += ' [SelItem]' } } catch {}
    $val = ''
    try { if ($el.GetCachedPropertyValue($A::IsValuePatternAvailableProperty)) { $val = ' value=' + [string]$el.GetCachedPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty) } } catch {}
    $help = $el.Cached.HelpText; if ($help) { $help = ' help=' + $help } else { $help = '' }
    $line = ('{0}{1} name="{2}" id="{3}" class="{4}" rect={5}{6}{7}{8}' -f ('  ' * $depth), $ct, $name, $aid, $cls, $rs, $flags, $val, $help)
    [void]$sb.AppendLine($line)
    if ($name -match $interes -or $aid -match $interes -or $help -match $interes) { [void]$hits.AppendLine($line.TrimStart()) }
    foreach ($ch in $el.CachedChildren) { Vuelca $ch ($depth + 1) $sb $hits }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($w in $wins) {
    if ((-not $w.Visible -and -not $IncluirOcultas) -or $w.Cloaked -ne 0 -or $w.W -le 0) { continue }
    if ($SoloTitulo -and ($w.Title -notmatch $SoloTitulo)) { continue }
    if ($w.Title -eq '') { continue }
    Write-Host ''
    Write-Host ("=== hwnd {0} '{1}'" -f $w.H, $w.Title)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $root = $A::FromHandle($w.H)
        # calentamiento: Chromium arma el arbol de accesibilidad recien cuando alguien lo pide
        [void]$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        Start-Sleep -Milliseconds 1500
        $el = $root.GetUpdatedCache($cr)
        $t1 = $sw.ElapsedMilliseconds
        $sb = New-Object System.Text.StringBuilder; $hits = New-Object System.Text.StringBuilder
        $script:count = 0
        Vuelca $el 0 $sb $hits
        $slug = ($w.Title -replace '[^A-Za-z0-9]+', '-').Trim('-')
        if ($slug.Length -gt 50) { $slug = $slug.Substring(0, 50) }
        $view = 'control'; if ($Raw) { $view = 'raw' }
        $file = Join-Path $OutDir ("{0}_{1}_{2}_{3}.txt" -f $stamp, $w.H, $view, $slug)
        $hdr = "# {0}`n# pid={1} hwnd={2} class={3} rect={4},{5} {6}x{7}`n# nodos={8} fetch={9}ms total={10}ms vista={11}`n`n" -f $w.Title, $w.Pid, $w.H, $w.Class, $w.X, $w.Y, $w.W, $w.Hh, $script:count, $t1, $sw.ElapsedMilliseconds, $view
        [System.IO.File]::WriteAllText($file, $hdr + $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
        Write-Host ("  nodos={0} fetch={1}ms total={2}ms -> {3}" -f $script:count, $t1, $sw.ElapsedMilliseconds, $file)
        Write-Host '  --- nodos de interes ---'
        Write-Host $hits.ToString()
    } catch {
        Write-Host ("  ERROR: {0}" -f $_.Exception.Message)
    }
}
