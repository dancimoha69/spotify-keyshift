$ErrorActionPreference = "Stop"

$setup = Join-Path $PSScriptRoot "release\Distribution\KeyShiftSetup.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "Не найден установщик $setup. Сначала собери пакет командой Build-Distribution.ps1."
}

Start-Process -FilePath $setup -Wait
