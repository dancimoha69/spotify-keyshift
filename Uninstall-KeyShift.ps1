$ErrorActionPreference = "Stop"

$uninstaller = Join-Path $env:LOCALAPPDATA "KeyShiftBridge\Uninstall.exe"
if (-not (Test-Path -LiteralPath $uninstaller)) {
    throw "Установщик Key Shift не найден. Удали приложение через Параметры Windows → Приложения."
}

Start-Process -FilePath $uninstaller -ArgumentList "--uninstall" -Wait
