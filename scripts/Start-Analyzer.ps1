param([switch]$BuildOnly, [switch]$Rebuild, [switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$binaryDirectory = Join-Path $projectRoot 'Aimmy2\bin\x64\Release\net8.0-windows'
$applicationExe = Join-Path $binaryDirectory 'YmmiaV2.exe'
$applicationDll = Join-Path $binaryDirectory 'YmmiaV2.dll'
$needsBuild = $BuildOnly -or $Rebuild -or !(Test-Path -LiteralPath $applicationExe) -or !(Test-Path -LiteralPath $applicationDll)
$localSdk = Join-Path $env:LOCALAPPDATA 'AutoAimmyBuild\dotnet\dotnet.exe'
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$candidates = @($localSdk)
if ($systemDotnet) { $candidates += $systemDotnet.Source }
$dotnetExe = $null
foreach ($candidate in ($candidates | Select-Object -Unique)) {
    if (!$needsBuild) { break }
    if (!(Test-Path -LiteralPath $candidate)) { continue }
    $installedSdks = @(& $candidate --list-sdks)
    if ($LASTEXITCODE -eq 0 -and ($installedSdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 })) {
        $dotnetExe = $candidate
        break
    }
}
if ($needsBuild -and !$dotnetExe) {
    throw 'No .NET SDK 8 or newer was found. A .NET runtime alone cannot compile Aimmy. Install a .NET SDK and retry.'
}
$previousDotnetRoot = $env:DOTNET_ROOT
$previousDotnetRootX64 = $env:DOTNET_ROOT_X64
Push-Location $projectRoot
try {
    if ($needsBuild) {
        $env:DOTNET_ROOT = Split-Path -Parent $dotnetExe
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        Write-Host "Build SDK: $dotnetExe"
        & $dotnetExe build 'Aimmy2\Aimmy2.csproj' -c Release -p:Platform=x64 --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    } else {
        Write-Host 'Using compiled analyzer. No SDK required. Use -Rebuild after source changes.'
    }
    $configuration = Join-Path $projectRoot 'adaptive.json'
    if (!(Test-Path -LiteralPath $configuration)) { $configuration = Join-Path $projectRoot 'adaptive.example.json' }
    $options = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json
    if (!$options.Enabled -or !$options.OfflineTrainerConfirmed) { throw 'Analyzer launcher requires Enabled=true and OfflineTrainerConfirmed=true.' }
    Copy-Item -LiteralPath $configuration -Destination (Join-Path $binaryDirectory 'adaptive.json')
    if ($BuildOnly) {
        Write-Host 'Build verified. Observation configuration installed. Application not launched.'
        return
    }
    # Prefer the installed system runtime; compilation and launch have separate requirements.
    $runtimeCandidates = @()
    if ($systemDotnet) { $runtimeCandidates += $systemDotnet.Source }
    $runtimeCandidates += Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    $runtimeCandidates += $localSdk
    $runtimeExe = $null
    foreach ($candidate in ($runtimeCandidates | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        $installedRuntimes = @(& $candidate --list-runtimes)
        if ($LASTEXITCODE -eq 0 -and
            ($installedRuntimes | Where-Object { $_ -match '^Microsoft.NETCore.App 8\.' }) -and
            ($installedRuntimes | Where-Object { $_ -match '^Microsoft.WindowsDesktop.App 8\.' })) {
            $runtimeExe = $candidate
            break
        }
    }
    if (!$runtimeExe) { throw 'The compiled analyzer requires the Windows x64 .NET 8 Desktop Runtime.' }
    $env:DOTNET_ROOT = Split-Path -Parent $runtimeExe
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    Write-Host "Runtime: $runtimeExe"
    Write-Host "Application: $applicationExe"
    if ($CheckOnly) {
        Write-Host 'Launch checks passed. Observation configuration enabled. Application not launched.'
        return
    }
    Write-Host 'Observation only. Load a model for your offline trainer; recording begins when it is loaded.'
    Start-Process -FilePath $applicationExe -WorkingDirectory $binaryDirectory -WindowStyle Hidden
} finally {
    Pop-Location
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:DOTNET_ROOT_X64 = $previousDotnetRootX64
}
