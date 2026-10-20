$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "KeyShiftBridge.csproj"
$output = Join-Path (Split-Path -Parent $PSScriptRoot) "release\KeyShift"
$nuget = Join-Path (Split-Path -Parent $PSScriptRoot) ".nuget"
$localDotnet = Join-Path (Split-Path -Parent $PSScriptRoot) ".sdk\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet" }

New-Item -ItemType Directory -Force -Path $output, $nuget | Out-Null
$env:NUGET_PACKAGES = $nuget

& $dotnet publish $project `
  --configuration Release `
  --framework net10.0-windows10.0.19041.0 `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  --output $output

if ($LASTEXITCODE -ne 0) { throw "Сборка Key Shift завершилась с ошибкой." }
Write-Host "Готово: $output\KeyShift.exe"
