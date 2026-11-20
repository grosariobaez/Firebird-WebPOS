param([string]$Version = "1.0.4")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serviceProject = Join-Path $root "JomliaWebPosSync\JomliaWebPosSync.csproj"
$configuratorProject = Join-Path $root "JomliaWebPosSync.Configurator\JomliaWebPosSync.Configurator.csproj"
$publishDir = Join-Path $PSScriptRoot ".publish"
$configuratorPublishDir = Join-Path $PSScriptRoot ".publish-configurator"
$outputDir = Join-Path $PSScriptRoot "output"
$iss = Join-Path $PSScriptRoot "JomliaWebPosSync.iss"

foreach ($path in @($publishDir, $configuratorPublishDir)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $publishDir, $configuratorPublishDir, $outputDir | Out-Null

dotnet publish $serviceProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish del servicio falló con código $LASTEXITCODE" }

dotnet publish $configuratorProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $configuratorPublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish del configurador falló con código $LASTEXITCODE" }

Get-ChildItem -LiteralPath $configuratorPublishDir -File -Filter 'appsettings*.json' -ErrorAction SilentlyContinue | Remove-Item -Force
$configuratorAppSettings = Get-ChildItem -LiteralPath $configuratorPublishDir -Recurse -File -Filter 'appsettings*.json' -ErrorAction SilentlyContinue
if ($configuratorAppSettings) {
    throw "Empaquetado inválido: el configurador no debe incluir appsettings.json."
}

$iscc = $env:JOMLIA_INSTALLER_ISCC
if ([string]::IsNullOrWhiteSpace($iscc)) {
    $localIscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
    $programIscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    if (Test-Path -LiteralPath $localIscc) { $iscc = $localIscc }
    elseif (-not [string]::IsNullOrEmpty(${env:ProgramFiles(x86)}) -and (Test-Path -LiteralPath $programIscc)) { $iscc = $programIscc }
    else {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $iscc = $command.Source }
    }
}
if ([string]::IsNullOrWhiteSpace($iscc) -or -not (Test-Path -LiteralPath $iscc)) {
    throw "No se encontró ISCC.exe. Defina JOMLIA_INSTALLER_ISCC o instale Inno Setup 6."
}

& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC falló con código $LASTEXITCODE" }

Get-ChildItem -LiteralPath $outputDir -File | Where-Object { $_.Extension -eq ".exe" } | ForEach-Object { $_.FullName }
