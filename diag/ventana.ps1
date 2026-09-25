# ventana.ps1 — ShowWindow a mano sobre un hwnd (para sacar a Teams de la bandeja SIN robar el foco)
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File ventana.ps1 -Hwnd 84480020 -Cmd showmin
# Cmd: showmin (SW_SHOWMINNOACTIVE=7) | shownoact (SW_SHOWNOACTIVATE=4) | hide (0) | restore (9) | minimize (6) | info
param([Parameter(Mandatory)][long]$Hwnd, [string]$Cmd = 'info')
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
'@
$h = [IntPtr]$Hwnd
$codigos = @{ showmin = 7; shownoact = 4; hide = 0; restore = 9; minimize = 6 }
if ($codigos.ContainsKey($Cmd)) { [void][W.U]::ShowWindow($h, $codigos[$Cmd]); Start-Sleep -Milliseconds 300 }
'hwnd={0} visible={1} minimizada={2} foreground={3}' -f $Hwnd, [W.U]::IsWindowVisible($h), [W.U]::IsIconic($h), ([W.U]::GetForegroundWindow() -eq $h)
