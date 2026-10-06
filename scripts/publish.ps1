param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory=Join-Path $projectRoot 'dist/FinanceTracking-win-x64' }
$OutputDirectory=[System.IO.Path]::GetFullPath($OutputDirectory)
Push-Location $projectRoot
try {
    dotnet restore src/Finance.Desktop -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Desktop restore failed' }
    dotnet publish src/Finance.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o (Join-Path $OutputDirectory 'app')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
    dotnet restore src/Finance.Cli -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'CLI restore failed' }
    dotnet publish src/Finance.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o (Join-Path $OutputDirectory 'cli')
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $OutputDirectory -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
    $archive="$OutputDirectory.zip"
    Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $archive -Force
    Write-Output $archive
} finally { Pop-Location }
