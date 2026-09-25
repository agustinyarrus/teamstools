# make-icon.ps1 — el icono de TeamsTools: el boton de grabar que eligio el user (25-sep-2026).
# La fuente es fuente-icono-grabar.png (512 px, RGBA con transparencia). Este script la deja como icon-512.png y
# build.ps1 -Icono la convierte a app.ico (16..512) con png2ico. El dibujo viejo (la puerta con luz) quedo en
# make-icon-puerta.ps1.bak-2026-09-25 por si alguna vez se quiere volver.
# OJO: la BANDEJA no usa este icono: se dibuja en vivo segun el estado (Bandeja.cs) y al user le gusta asi.
param([string]$Out = "$PSScriptRoot\icon-512.png")
$ErrorActionPreference = 'Stop'
$fuente = Join-Path $PSScriptRoot 'fuente-icono-grabar.png'
if (-not (Test-Path $fuente)) { throw "no encuentro $fuente" }
Copy-Item $fuente $Out -Force
Write-Host "icono -> $Out"
