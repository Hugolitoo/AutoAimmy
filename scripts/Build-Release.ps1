param(
    [string]$Version = '0.1.1',
    [ValidateSet('test','stable')][string]$Channel = 'test',
    [string]$Repository = 'Hugolitoo/AutoAimmy'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Use a numeric version such as 0.1.1.' }
$localSdk = Join-Path $env:LOCALAPPDATA 'AutoAimmyBuild\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$releaseRoot = Join-Path $projectRoot ('artifacts\' + $Version)
if (Test-Path -LiteralPath $releaseRoot) { throw 'Release output exists. Use a new version or move the old output before rebuilding.' }
$publishDirectory = Join-Path $releaseRoot 'payload'
$null = New-Item -ItemType Directory -Path $publishDirectory -Force
Push-Location $projectRoot
try {
    & $dotnetExe run --project 'tests\AdaptiveChecks\AdaptiveChecks.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Analyzer checks failed.' }
    & $dotnetExe publish 'Aimmy2\Aimmy2.csproj' -c Release -r win-x64 --self-contained true -p:Platform=x64 "-p:Version=$Version" -p:PublishSingleFile=false -o $publishDirectory --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    foreach ($file in @('Client.ps1','Updater.psm1')) { Copy-Item -LiteralPath (Join-Path $projectRoot ('distribution\' + $file)) -Destination $publishDirectory }
    foreach ($file in @('LICENSE','SourceAvailable.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $publishDirectory }
    [ordered]@{ Product='AutoAimmy'; Version=$Version; Channel=$Channel; Runtime='win-x64'; ObservationOnly=$true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDirectory 'release.json') -Encoding UTF8
    # Only explicitly selected publish files are packaged; never include source build output's sessions or configuration.
    $forbidden = @('bin','data','sessions','profiles','adaptive.json','github-access.xml')
    foreach ($name in $forbidden) {
        if (Test-Path -LiteralPath (Join-Path $publishDirectory $name)) { throw "Personal data or mutable config in publish output: $name" }
    }
    foreach ($required in @('YmmiaV2.exe','YmmiaV2.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $required))) { throw "Incomplete self-contained build: $required" }
    }
    $updateZip = Join-Path $releaseRoot 'AutoAimmy-update-win-x64.zip'
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $updateZip -CompressionLevel Optimal
    [ordered]@{ Schema=1; Product='AutoAimmy'; Version=$Version; Channel=$Channel; Runtime='win-x64';
        Sha256=(Get-FileHash -LiteralPath $updateZip -Algorithm SHA256).Hash; Size=(Get-Item -LiteralPath $updateZip).Length } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot 'manifest.json') -Encoding UTF8
    $installRoot = Join-Path $releaseRoot 'AutoAimmy'
    $versionDirectory = Join-Path $installRoot ('versions\' + $Version)
    $null = New-Item -ItemType Directory -Path $versionDirectory -Force
    Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $versionDirectory -Recurse
    foreach ($file in @('bootstrap.ps1','README-AMI.txt')) { Copy-Item -LiteralPath (Join-Path $projectRoot ('distribution\' + $file)) -Destination $installRoot }
    [ordered]@{ Repository=$Repository; Channel=$Channel } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'updater.json') -Encoding UTF8
    [ordered]@{ Current=$Version; Previous=$null } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'current.json') -Encoding UTF8
    $dataDirectory = Join-Path $installRoot 'data'
    foreach ($relative in @('bin\models','bin\configs','bin\labels','sessions','profiles')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $dataDirectory $relative) -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'adaptive.example.json') -Destination (Join-Path $dataDirectory 'adaptive.json')
    $commands = [ordered]@{
        'AutoAimmy.cmd'=''; 'AutoAimmy-hors-ligne.cmd'='-Offline'; 'Mettre-a-jour.cmd'='-Action Update';
        'Retour-version-precedente.cmd'='-Action Rollback'; 'Exporter-rapport.cmd'='-Action Export'; 'Connexion-GitHub.cmd'='-Action Connect'; 'Configurer-viseur.cmd'='-Action Configure -Offline'
    }
    foreach ($name in $commands.Keys) {
        $text = '@echo off' + "`r`n" + 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0bootstrap.ps1" ' + $commands[$name] + "`r`n" + 'if errorlevel 1 pause' + "`r`n"
        if ($name -ne 'AutoAimmy.cmd' -and $name -ne 'AutoAimmy-hors-ligne.cmd') { $text += 'pause' + "`r`n" }
        [IO.File]::WriteAllText((Join-Path $installRoot $name), $text, [Text.Encoding]::ASCII)
    }
    $installerZip = Join-Path $releaseRoot 'AutoAimmy-win-x64.zip'
    Compress-Archive -LiteralPath $installRoot -DestinationPath $installerZip -CompressionLevel Optimal
    Write-Host "Ready to send: $installerZip"
    Write-Host "Release assets: $updateZip ; $(Join-Path $releaseRoot 'manifest.json')"
} finally { Pop-Location }
