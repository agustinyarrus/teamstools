# despertar-teams.ps1 — hace que la ventana principal de Teams (cerrada a la bandeja) monte su contenido
# sin robar el foco: 1) la mueve fuera de pantalla y la muestra sin activar, 2) si no monta, la muestra en
# su lugar 3 s, 3) la deja visible+minimizada (el arbol UIA sobrevive minimizado) y vuelve la posicion original.
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File despertar-teams.ps1 [-Hwnd 84480020]
param([long]$Hwnd = 0)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
'@
function Nodos([IntPtr]$h) {
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        return $all.Count
    } catch { return -1 }
}
# elegir la ventana principal: TeamsWebView con titulo exactamente "Microsoft Teams" y la mas grande
if ($Hwnd -eq 0) {
    $pids = @(Get-Process ms-teams -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
    $cands = New-Object System.Collections.ArrayList
    $cb = [W.U+EnumProc]{ param($h, $l)
        [uint32]$p = 0; [void][W.U]::GetWindowThreadProcessId($h, [ref]$p)
        if ($pids -contains $p) {
            $sb = New-Object System.Text.StringBuilder 512; [void][W.U]::GetWindowText($h, $sb, 512)
            $cl = New-Object System.Text.StringBuilder 256; [void][W.U]::GetClassName($h, $cl, 256)
            if ($cl.ToString() -eq 'TeamsWebView' -and $sb.ToString() -match 'Microsoft Teams') {
                $pl = New-Object W.U+WINDOWPLACEMENT; $pl.length = [System.Runtime.InteropServices.Marshal]::SizeOf($pl); [void][W.U]::GetWindowPlacement($h, [ref]$pl)
                $w = $pl.normal.R - $pl.normal.L; $hh = $pl.normal.B - $pl.normal.T
                [void]$cands.Add([pscustomobject]@{ H = $h; Titulo = $sb.ToString(); W = $w; Hh = $hh; Area = $w * $hh })
            }
        }
        return $true }
    [void][W.U]::EnumWindows($cb, [IntPtr]::Zero)
    $best = $cands | Sort-Object Area -Descending | Select-Object -First 1
    if (-not $best) { 'no encontre la ventana principal de Teams'; exit 1 }
    $Hwnd = [long]$best.H
    'candidatas:'; $cands | ForEach-Object { '  {0} {1}x{2} {3}' -f $_.H, $_.W, $_.Hh, $_.Titulo }
}
$h = [IntPtr]$Hwnd
$fg0 = [W.U]::GetForegroundWindow()
$pl = New-Object W.U+WINDOWPLACEMENT; $pl.length = [System.Runtime.InteropServices.Marshal]::SizeOf($pl); [void][W.U]::GetWindowPlacement($h, [ref]$pl)
$x0 = $pl.normal.L; $y0 = $pl.normal.T; $w0 = $pl.normal.R - $pl.normal.L; $h0 = $pl.normal.B - $pl.normal.T
'inicio: hwnd={0} visible={1} min={2} normal={3},{4} {5}x{6} nodos={7}' -f $Hwnd, [W.U]::IsWindowVisible($h), [W.U]::IsIconic($h), $x0, $y0, $w0, $h0, (Nodos $h)

$SWP_NOACTIVATE = 0x10; $SWP_NOZORDER = 0x4; $SWP_NOSIZE = 0x1
# 1) fuera de pantalla + mostrar sin activar
[void][W.U]::SetWindowPos($h, [IntPtr]::Zero, -4000, 100, 0, 0, ($SWP_NOACTIVATE -bor $SWP_NOZORDER -bor $SWP_NOSIZE))
[void][W.U]::ShowWindow($h, 4)   # SW_SHOWNOACTIVATE
$n = -1
for ($i = 0; $i -lt 12; $i++) { Start-Sleep -Milliseconds 500; $n = Nodos $h; if ($n -gt 100) { break } }
'paso 1 (fuera de pantalla, sin activar): nodos={0} foco intacto={1}' -f $n, ([W.U]::GetForegroundWindow() -eq $fg0)
if ($n -le 100) {
    # 2) en pantalla, sin activar, 3 s
    [void][W.U]::SetWindowPos($h, [IntPtr]::Zero, $x0, $y0, 0, 0, ($SWP_NOACTIVATE -bor $SWP_NOZORDER -bor $SWP_NOSIZE))
    [void][W.U]::ShowWindow($h, 4)
    for ($i = 0; $i -lt 12; $i++) { Start-Sleep -Milliseconds 500; $n = Nodos $h; if ($n -gt 100) { break } }
    'paso 2 (en pantalla, sin activar): nodos={0} foco intacto={1}' -f $n, ([W.U]::GetForegroundWindow() -eq $fg0)
}
# 3) minimizar sin activar y devolver la posicion normal
[void][W.U]::ShowWindow($h, 7)   # SW_SHOWMINNOACTIVE
Start-Sleep -Milliseconds 800
$pl2 = New-Object W.U+WINDOWPLACEMENT; $pl2.length = [System.Runtime.InteropServices.Marshal]::SizeOf($pl2); [void][W.U]::GetWindowPlacement($h, [ref]$pl2)
if ($pl2.normal.L -ne $x0) { [void][W.U]::SetWindowPos($h, [IntPtr]::Zero, $x0, $y0, 0, 0, ($SWP_NOACTIVATE -bor $SWP_NOZORDER -bor $SWP_NOSIZE)) }
'fin: visible={0} min={1} nodos={2} foco intacto={3}' -f [W.U]::IsWindowVisible($h), [W.U]::IsIconic($h), (Nodos $h), ([W.U]::GetForegroundWindow() -eq $fg0)
