# probar-f15.ps1 — confirma que un pulso de la tecla fantasma F15 reinicia el reloj de inactividad de Windows
# (el mismo que usa Teams para marcar Ausente), SIN mover el mouse ni escribir nada visible.
Add-Type -Namespace F -Name W -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
[DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cb);
public static double IdleMs() { var li = new LASTINPUTINFO(); li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)); GetLastInputInfo(ref li); uint ahora = unchecked((uint)Environment.TickCount); return unchecked(ahora - li.dwTime); }
public static void F15() { var i = new INPUT[2]; i[0].type = 1; i[0].u.ki = new KEYBDINPUT { wVk = 0x7E }; i[1].type = 1; i[1].u.ki = new KEYBDINPUT { wVk = 0x7E, dwFlags = 2 }; SendInput(2, i, Marshal.SizeOf(typeof(INPUT))); }
'@
'esperando 3 s de inactividad real (no toques nada)...'
Start-Sleep -Seconds 3
$antes = [F.W]::IdleMs()
[F.W]::F15()
Start-Sleep -Milliseconds 150
$despues = [F.W]::IdleMs()
'idle antes = {0:N0} ms · despues del pulso F15 = {1:N0} ms · {2}' -f $antes, $despues, $(if ($antes -ge 2500 -and $despues -lt 500) { 'REINICIA (la F15 mantiene Disponible, sin mouse)' } elseif ($antes -lt 2500) { 'inconcluso: hubo actividad real' } else { 'NO reinicia' })
