# build.ps1 — compila TeamsTools (net48, exe unico sin dependencias) en dist\
#   .\build.ps1                 -> compila
#   .\build.ps1 -Run            -> compila y lo abre
#   .\build.ps1 -Icono          -> regenera assets\app.ico (Python + Pillow) antes de compilar
#   .\build.ps1 -Herramientas   -> ademas compila loopcap (Go, con -trimpath) y copia los scripts de Python a dist\tools\
#   .\build.ps1 -Salida <dir>   -> compila a otra carpeta (para probar sin tocar el exe que esta corriendo)
param([switch]$Run, [switch]$Icono, [switch]$Herramientas, [string]$Salida = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ($Icono) {
    & "$root\assets\make-icon.ps1"
    Push-Location "$root\assets"
    python "$root\assets\png2ico.py" icon-512.png app.ico
    Pop-Location
}
$args = @("$root\TeamsTools.csproj", '-c', 'Release', '-nologo', '-v', 'q')
if ($Salida) { $args += @('-o', $Salida) }
dotnet build @args
if ($LASTEXITCODE -ne 0) { Write-Host "build FALLO" -ForegroundColor Red; exit 1 }
$dist = if ($Salida) { $Salida } else { Join-Path $root 'dist' }
$exe = Join-Path $dist 'TeamsTools.exe'
$fi = Get-Item $exe
Write-Host ("OK -> {0}  {1:N0} bytes  {2}" -f $fi.FullName, $fi.Length, $fi.LastWriteTime)

if ($Herramientas) {
    # las herramientas de afuera viven en dist\tools\ (o donde apunte grabador.json / config.json).
    # Si dist\tools\<x> es un junction a la copia viva de desarrollo, se respeta y no se pisa nada.
    $tools = Join-Path $dist 'tools'
    function EsEnlace([string]$p) { (Test-Path $p) -and (((Get-Item $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) }
    $lc = Join-Path $tools 'loopcap'
    if (EsEnlace $lc) { Write-Host "tools\loopcap es un enlace: no lo toco" }
    else {
        New-Item -ItemType Directory -Force $lc | Out-Null
        Push-Location "$root\tools\loopcap"
        # -trimpath: el exe no lleva las rutas de la maquina donde se compilo
        go build -trimpath -ldflags '-s -w' -o (Join-Path $lc 'loopcap.exe') .
        Pop-Location
        if ($LASTEXITCODE -ne 0) { Write-Host "loopcap FALLO (hace falta Go 1.24+)" -ForegroundColor Red; exit 1 }
        Write-Host ("OK -> {0}" -f (Join-Path $lc 'loopcap.exe'))
    }
    foreach ($d in 'transcribe', 'teams-chats') {
        $dest = Join-Path $tools $d
        if (EsEnlace $dest) { Write-Host "tools\$d es un enlace: no lo toco"; continue }
        New-Item -ItemType Directory -Force $dest | Out-Null
        Copy-Item "$root\tools\$d\*.py" $dest -Force
        Write-Host ("OK -> {0}  ({1} scripts)" -f $dest, @(Get-ChildItem $dest -Filter *.py).Count)
    }
}
if ($Run) { Start-Process $exe }
