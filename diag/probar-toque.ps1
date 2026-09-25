# probar-toque.ps1 — verifica que cada metodo de "toque" reinicia el reloj de inactividad de Windows
# (el mismo que usa Teams para marcar Ausente). Correr cuando el mouse/teclado esten quietos 3+ segundos.
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File probar-toque.ps1
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Toque {
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cb);
  public static double IdleMs() { var li = new LASTINPUTINFO(); li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)); GetLastInputInfo(ref li); uint ahora = unchecked((uint)Environment.TickCount); return unchecked(ahora - li.dwTime); }
  public static void Mouse(int dx, int dy) { var i = new INPUT[1]; i[0].type = 0; i[0].u.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = 1 }; SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); }
  public static void Tecla(ushort vk) { var i = new INPUT[2]; i[0].type = 1; i[0].u.ki = new KEYBDINPUT { wVk = vk }; i[1].type = 1; i[1].u.ki = new KEYBDINPUT { wVk = vk, dwFlags = 2 }; SendInput(2, i, Marshal.SizeOf(typeof(INPUT))); }
}
"@
function Probar($nombre, [scriptblock]$accion) {
    # esperar hasta 90 s a que el user deje quieto el mouse/teclado 3 s seguidos
    $limite = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $limite -and [Toque]::IdleMs() -lt 3000) { Start-Sleep -Milliseconds 250 }
    $antes = [Toque]::IdleMs()
    & $accion
    Start-Sleep -Milliseconds 150
    $despues = [Toque]::IdleMs()
    $ok = ($antes -ge 2000) -and ($despues -lt 500)
    '{0,-24} idle antes={1,6:N0} ms  despues={2,5:N0} ms  -> {3}' -f $nombre, $antes, $despues, $(if ($ok) { 'REINICIA' } elseif ($antes -lt 2000) { 'inconcluso (habia actividad real)' } else { 'NO reinicia' })
}
Probar 'mouse 0 px'          { [Toque]::Mouse(0, 0) }
Probar 'mouse 1 px ida/vuelta' { [Toque]::Mouse(1, 0); Start-Sleep -Milliseconds 30; [Toque]::Mouse(-1, 0) }
Probar 'tecla F15'           { [Toque]::Tecla(0x7E) }
