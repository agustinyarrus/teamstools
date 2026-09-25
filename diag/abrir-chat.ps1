# abrir-chat.ps1 — abre (o crea) un chat de Teams por deep link, con texto opcional precargado (NO lo envia)
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File abrir-chat.ps1 -Con nombre.apellido@empresa.com [-Mensaje 'hola']
param([Parameter(Mandatory)][string]$Con, [string]$Mensaje = '')
Add-Type -AssemblyName System.Web
$url = 'msteams:/l/chat/0/0?users=' + [System.Uri]::EscapeDataString($Con)
if ($Mensaje) { $url += '&message=' + [System.Uri]::EscapeDataString($Mensaje) }
"abriendo $url"
Start-Process $url
