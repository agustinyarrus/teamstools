# prueba-invisible.ps1 — mide si el envio muestra ALGO en pantalla.
# Simula "Teams cerrado a la bandeja" (oculta sus ventanas), manda un mensaje con la app y muestrea
# cada 150 ms si alguna ventana de Teams queda visible DENTRO de la pantalla. Maximo 0 = 100% invisible.
param([string]$Regla = 'saludo')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace P -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
'@
function VentanasTeams {
    $pids = @(Get-Process ms-teams -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
    $res = New-Object System.Collections.ArrayList
    if ($pids.Count -eq 0) { return $res }
    $cb = [P.W+EnumProc]{ param($h, $l)
        [uint32]$pp = 0; [void][P.W]::GetWindowThreadProcessId($h, [ref]$pp)
        if ($pids -contains $pp) {
            $cl = New-Object System.Text.StringBuilder 128; [void][P.W]::GetClassName($h, $cl, 128)
            if ($cl.ToString() -eq 'TeamsWebView') {
                $t = New-Object System.Text.StringBuilder 400; [void][P.W]::GetWindowText($h, $t, 400)
                $r = New-Object P.W+RECT; [void][P.W]::GetWindowRect($h, [ref]$r)
                [void]$res.Add([pscustomobject]@{ H = $h; Titulo = $t.ToString(); X = $r.L; Y = $r.T; W = $r.R - $r.L; Hh = $r.B - $r.T; Vis = [P.W]::IsWindowVisible($h); Min = [P.W]::IsIconic($h) })
            }
        }
        return $true }
    [void][P.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $res
}
$pant = [System.Windows.Forms.SystemInformation]::VirtualScreen
function VisibleEnPantalla {
    $n = 0; $quien = @()
    foreach ($v in (VentanasTeams)) {
        if (-not $v.Vis -or $v.Min) { continue }
        if ($v.W -le 0 -or $v.Hh -le 0) { continue }
        $der = $v.X + $v.W; $aba = $v.Y + $v.Hh
        if ($der -gt $pant.Left -and $v.X -lt $pant.Right -and $aba -gt $pant.Top -and $v.Y -lt $pant.Bottom) { $n++; $quien += ("{0} {1}x{2}@{3},{4}" -f $v.Titulo, $v.W, $v.Hh, $v.X, $v.Y) }
    }
    return @{ N = $n; Quien = $quien }
}

'=== estado inicial'
$antes = VentanasTeams
$antes | ForEach-Object { '  {0} vis={1} min={2} {3}x{4}@{5},{6} "{7}"' -f $_.H, $_.Vis, $_.Min, $_.W, $_.Hh, $_.X, $_.Y, $_.Titulo }
$fg0 = [P.W]::GetForegroundWindow()
$t = New-Object System.Text.StringBuilder 300; [void][P.W]::GetWindowText($fg0, $t, 300)
'  foreground inicial: {0} "{1}"' -f $fg0, $t

'=== simulo Teams cerrado a la bandeja (oculto sus ventanas)'
foreach ($v in $antes) { [void][P.W]::ShowWindow($v.H, 0) }   # SW_HIDE
Start-Sleep -Milliseconds 700
$chk = VisibleEnPantalla
'  ventanas de Teams visibles ahora: {0}' -f $chk.N

'=== mando el mensaje y muestreo la pantalla cada 150 ms'
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\TeamsTools.exe'
$p = Start-Process -FilePath $exe -ArgumentList '--probar-regla', $Regla -PassThru
$maximo = 0; $vistos = @(); $muestras = 0
while (-not $p.HasExited) {
    Start-Sleep -Milliseconds 150
    $c = VisibleEnPantalla
    $muestras++
    if ($c.N -gt $maximo) { $maximo = $c.N }
    foreach ($q in $c.Quien) { if ($vistos -notcontains $q) { $vistos += $q } }
}
Start-Sleep -Milliseconds 400
'  muestras: {0} · MAXIMO de ventanas de Teams visibles en pantalla: {1}' -f $muestras, $maximo
if ($vistos.Count -gt 0) { '  se vio:'; $vistos | ForEach-Object { '    ' + $_ } } else { '  NUNCA se vio nada de Teams en pantalla' }

'=== estado final'
$fin = VentanasTeams
$fin | ForEach-Object { '  {0} vis={1} min={2} {3}x{4}@{5},{6} "{7}"' -f $_.H, $_.Vis, $_.Min, $_.W, $_.Hh, $_.X, $_.Y, $_.Titulo }
$fgf = [P.W]::GetForegroundWindow()
$t2 = New-Object System.Text.StringBuilder 300; [void][P.W]::GetWindowText($fgf, $t2, 300)
'  foreground final: {0} "{1}" · volvio al inicial: {2}' -f $fgf, $t2, ($fgf -eq $fg0)

'=== dejo Teams como estaba (posiciones originales, minimizado)'
foreach ($v in $antes) {
    [void][P.W]::SetWindowPos($v.H, [IntPtr]::Zero, $v.X, $v.Y, 0, 0, (0x1 -bor 0x4 -bor 0x10))
    if ($v.Vis) { [void][P.W]::ShowWindow($v.H, 7) } else { [void][P.W]::ShowWindow($v.H, 0) }
}
'listo'
