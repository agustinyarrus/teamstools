# limpiar-ime.ps1 — vuelve a ocultar las ventanas auxiliares de Teams (Default IME, MSCTFIME UI, DDE, GDI+, Rtc…)
# que quedaron visibles por error. NO toca las ventanas reales de Teams (clase TeamsWebView).
param([switch]$SoloMirar)
Add-Type -Namespace L -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
'@
$pids = @(Get-Process ms-teams -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
if ($pids.Count -eq 0) { 'Teams no esta corriendo'; exit }
$todas = New-Object System.Collections.ArrayList
$cb = [L.W+EnumProc]{ param($h, $l)
    [uint32]$pp = 0; [void][L.W]::GetWindowThreadProcessId($h, [ref]$pp)
    if ($pids -contains $pp) {
        $cl = New-Object System.Text.StringBuilder 128; [void][L.W]::GetClassName($h, $cl, 128)
        $t = New-Object System.Text.StringBuilder 400; [void][L.W]::GetWindowText($h, $t, 400)
        $r = New-Object L.W+RECT; [void][L.W]::GetWindowRect($h, [ref]$r)
        [void]$todas.Add([pscustomobject]@{ H = $h; Clase = $cl.ToString(); Titulo = $t.ToString(); Vis = [L.W]::IsWindowVisible($h); Min = [L.W]::IsIconic($h); W = $r.R - $r.L; Hh = $r.B - $r.T })
    }
    return $true }
[void][L.W]::EnumWindows($cb, [IntPtr]::Zero)

'=== todas las ventanas de Teams'
$todas | ForEach-Object { '  {0,-10} vis={1,-5} min={2,-5} {3,5}x{4,-5} clase={5,-28} "{6}"' -f $_.H, $_.Vis, $_.Min, $_.W, $_.Hh, $_.Clase, $_.Titulo }

# auxiliares que NUNCA deberian estar visibles
$intrusas = $todas | Where-Object { $_.Vis -and $_.Clase -ne 'TeamsWebView' }
''
if ($intrusas.Count -eq 0) { 'OK: no hay ventanas auxiliares visibles'; exit }
'=== auxiliares visibles que no deberian estarlo: {0}' -f $intrusas.Count
$intrusas | ForEach-Object { '  {0} clase={1} "{2}"' -f $_.H, $_.Clase, $_.Titulo }
if ($SoloMirar) { ''; '(solo mirar: no toco nada)'; exit }
foreach ($v in $intrusas) { [void][L.W]::ShowWindow($v.H, 0) }   # SW_HIDE
Start-Sleep -Milliseconds 400
$quedan = 0
foreach ($v in $intrusas) { if ([L.W]::IsWindowVisible($v.H)) { $quedan++ } }
''
'ocultadas {0} · quedan visibles {1}' -f $intrusas.Count, $quedan
