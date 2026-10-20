$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$app = Join-Path $root "release\KeyShift\KeyShift.exe"
$output = Join-Path $root "release\Distribution"

& (Join-Path $root "KeyShiftBridge\Build-KeyShift.ps1")
if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать Key Shift." }

New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath $app -Destination (Join-Path $output "KeyShiftSetup.exe") -Force
Get-ChildItem -LiteralPath $output -Filter "*.pdb" -File | Remove-Item -Force

$readme = @'
KEY SHIFT ДЛЯ SPOTIFY — УСТАНОВКА

Требования: 64-разрядная Windows 10 (сборка 19041 или новее) и классический Spotify Desktop с сайта spotify.com. Spicetify не нужен.

1. Полностью закройте Spotify.
2. Запустите KeyShiftSetup.exe и нажмите «Установить».
3. После установки перезапустите Spotify. Кнопка Key Shift появится в панели плеера.

Удаление: откройте Параметры Windows → Приложения → Установленные приложения и удалите «Key Shift for Spotify».

Установщик сохранит исходный файл интерфейса Spotify и вернёт его при удалении Key Shift. Если Spotify обновит интерфейс и кнопка исчезнет, запустите установщик ещё раз.

Key Shift не запрашивает пароль от Spotify. Изменение интерфейса неофициальное; Microsoft Store версия Spotify не поддерживается.
'@
Set-Content -LiteralPath (Join-Path $output "README.txt") -Value $readme -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $output -Force

$zip = Join-Path $root "release\KeyShift-Setup.zip"
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -Force
Write-Host "Готово: $output\KeyShiftSetup.exe"
Write-Host "Архив для передачи: $zip"
