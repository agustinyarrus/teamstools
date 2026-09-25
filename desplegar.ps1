# desplegar.ps1 - pone un TeamsTools.exe nuevo en dist SIN cortar nada.
#
#   .\desplegar.ps1 -Exe <ruta del exe nuevo> [-EsperarMinutos 120] [-SoloRevisar]
#
#   1. ESPERA a que sea seguro: sin llamada (el vigia no esta en una reunion), sin grabacion en curso ni cerrandose,
#      sin loopcap vivo, y con estado.json fresco (la app anda). Si esta transcribiendo de fondo NO espera: el cierre
#      ordenado guarda lo hecho y el exe nuevo retoma desde el ultimo trozo verificado.
#   2. El exe en uso se RENOMBRA a dist\_anterior\TeamsTools-AAAA-MM-DD-HHmm.exe (a un exe corriendo se lo puede
#      renombrar, no pisar) y se copia el nuevo en su lugar.
#   3. Al viejo se le pide que se cierre ORDENADO (--cerrar: corta limpio y vacia el disco, ~30 s) y se abre el nuevo
#      en la bandeja (--min).
#   4. Verifica que el proceso nuevo este vivo, que sea ESE archivo y que escriba estado.json.
#   Si algo falla despues de renombrar, vuelve a poner el exe viejo.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$EsperarMinutos = 120,
    [switch]$SoloRevisar
)
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$destino = Join-Path $dist 'TeamsTools.exe'
$anterior = Join-Path $dist '_anterior'
$estadoJson = Join-Path $dist 'datos\estado.json'
$enLlamada = @('EnLlamada', 'EsperandoGente', 'SalaVacia', 'Pospuesto', 'Saliendo')

function Linea([string]$t, [string]$c = 'Gray') { Write-Host ("  " + $t) -ForegroundColor $c }

function Estado {
    try {
        $e = Get-Content $estadoJson -Raw -Encoding UTF8 | ConvertFrom-Json
        $edad = ((Get-Date) - [datetime]::ParseExact($e.hora, 'yyyy-MM-dd HH:mm:ss', $null)).TotalSeconds
        $loopcap = @(Get-Process loopcap -ErrorAction SilentlyContinue).Count
        $motivo = ''
        if ($edad -gt 90) { $motivo = "estado.json viejo ($([int]$edad) s): la app no reporta" }
        elseif ($enLlamada -contains $e.estado) { $motivo = "en una reunion ($($e.estado): $($e.reunion))" }
        elseif ($e.grabador.reunion) { $motivo = "grabando '$($e.grabador.reunion)' ($($e.grabador.segundos) s)" }
        elseif ($e.grabador.cerrando) { $motivo = "cerrando la grabacion '$($e.grabador.cerrando)'" }
        elseif ($loopcap -gt 0) { $motivo = "hay $loopcap loopcap vivo/s" }
        [pscustomobject]@{ Seguro = ($motivo -eq ''); Motivo = $motivo; Cola = $e.grabador.enCola; Estado = $e.estado }
    }
    catch { [pscustomobject]@{ Seguro = $false; Motivo = "no pude leer estado.json: $($_.Exception.Message)"; Cola = -1; Estado = '?' } }
}

if (-not (Test-Path $Exe)) { throw "no existe el exe nuevo: $Exe" }
$nuevo = Get-Item $Exe
Write-Host ""
Write-Host "  desplegar TeamsTools" -ForegroundColor Cyan
Linea ("exe nuevo : {0}  ({1:N0} bytes, {2:dd/MM HH:mm:ss})" -f $nuevo.FullName, $nuevo.Length, $nuevo.LastWriteTime)

# --- 1. esperar a que sea seguro
$limite = (Get-Date).AddMinutes($EsperarMinutos)
$ultimo = ''
while ($true) {
    $s = Estado
    if ($s.Seguro) { Linea ("seguro    : sin llamada ni grabacion - cola del grabador: {0}" -f $s.Cola) 'Green'; break }
    if ($s.Motivo -ne $ultimo) { Linea ("espero    : {0}  [{1:HH:mm:ss}]" -f $s.Motivo, (Get-Date)) 'Yellow'; $ultimo = $s.Motivo }
    if ($SoloRevisar) { exit 3 }
    if ((Get-Date) -gt $limite) { Linea "no se dio la ventana en $EsperarMinutos min: no toco nada" 'Red'; exit 2 }
    Start-Sleep -Seconds 15
}
if ($SoloRevisar) { exit 0 }

# --- 2. renombrar el que corre y copiar el nuevo
New-Item -ItemType Directory -Force $anterior | Out-Null
$guardado = Join-Path $anterior ("TeamsTools-{0:yyyy-MM-dd-HHmm}.exe" -f (Get-Date))
Move-Item $destino $guardado -Force
Linea "viejo     : -> $guardado"
try {
    Copy-Item $nuevo.FullName $destino -Force
    $viejos = @(Get-Process TeamsTools -ErrorAction SilentlyContinue)
    # --- 3. cierre ordenado del viejo y arranque del nuevo
    $r = & $destino --cerrar | Out-String
    Linea ("cerrar    : {0}" -f $r.Trim())
    foreach ($p in $viejos) { try { $p.WaitForExit(60000) | Out-Null } catch { Linea "no pude esperar al pid $($p.Id): $($_.Exception.Message)" 'Yellow' } }
    if (@(Get-Process TeamsTools -ErrorAction SilentlyContinue | Where-Object { $viejos.Id -contains $_.Id }).Count -gt 0) { throw "el viejo no se cerro en 60 s" }
    $antes = Get-Date
    Start-Process $destino -ArgumentList '--min' | Out-Null
    # --- 4. verificar
    $ok = $false
    for ($i = 0; $i -lt 40 -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 500
        $p = Get-Process TeamsTools -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $antes.AddSeconds(-2) } | Select-Object -First 1
        if ($p -and $p.Path -eq $destino -and (Get-Item $estadoJson).LastWriteTime -gt $antes) { $ok = $true }
    }
    if (-not $ok) { throw "el nuevo no arranco o no escribe estado.json" }
    Linea ("nuevo     : pid {0} - {1} - estado.json al dia" -f $p.Id, $p.Path) 'Green'
}
catch {
    Linea ("FALLO     : {0} -> vuelvo a poner el viejo" -f $_.Exception.Message) 'Red'
    if (-not (Get-Process TeamsTools -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $destino })) {
        Copy-Item $guardado $destino -Force
        Start-Process $destino -ArgumentList '--min' | Out-Null
    }
    exit 1
}
Write-Host ""
exit 0
