Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SafeVersion {
    param([string]$Version)
    if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Invalid version.' }
    return [version]$Version
}

function Write-State {
    param([string]$Root, $State)
    $path = Join-Path $Root 'current.json'
    $temporary = Join-Path $Root ('state-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $State | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    if (Test-Path -LiteralPath $path) {
        # Windows PowerShell marshals $null to an empty backup path for this overload.
        $backup = Join-Path $Root ('previous-state-' + [guid]::NewGuid().ToString('N') + '.json')
        [IO.File]::Replace($temporary, $path, $backup)
    } else { [IO.File]::Move($temporary, $path) }
}

function Get-InstalledState {
    param([string]$Root)
    $state = Get-Content -LiteralPath (Join-Path $Root 'current.json') -Raw | ConvertFrom-Json
    $null = Get-SafeVersion $state.Current
    if ($state.Previous) { $null = Get-SafeVersion $state.Previous }
    return $state
}

function Assert-AppStopped {
    param([string]$Root)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    foreach ($process in @(Get-Process -Name YmmiaV2 -ErrorAction SilentlyContinue)) {
        try { $processPath = $process.Path } catch { throw 'Cannot verify that AutoAimmy is closed.' }
        if (!$processPath -or $processPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close AutoAimmy before updating or rolling back.'
        }
    }
}

function Get-UpdateHeaders {
    param([string]$Root, [string]$Accept = 'application/vnd.github+json')
    $headers = @{ 'User-Agent' = 'AutoAimmy-Updater'; 'Accept' = $Accept; 'X-GitHub-Api-Version' = '2022-11-28' }
    $credential = Join-Path $Root 'data\github-access.xml'
    if (Test-Path -LiteralPath $credential) {
        $saved = Import-Clixml -LiteralPath $credential
        if ($saved -isnot [Management.Automation.PSCredential]) { throw 'Invalid GitHub credential.' }
        $headers.Authorization = 'Bearer ' + $saved.GetNetworkCredential().Password
    }
    return $headers
}

function Get-RemoteUpdate {
    param([string]$Root)
    $config = Get-Content -LiteralPath (Join-Path $Root 'updater.json') -Raw | ConvertFrom-Json
    if (!$config.Repository) { return $null }
    if ($config.Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
    if ($config.Channel -notin @('test', 'stable')) { throw 'Invalid release channel.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $uri = 'https://api.github.com/repos/' + $config.Repository + '/releases?per_page=30'
    $releases = @(Invoke-RestMethod -Uri $uri -Headers (Get-UpdateHeaders $Root) -TimeoutSec 15)
    $compatible = @($releases | Where-Object {
        !$_.draft -and ($config.Channel -eq 'test' -or !$_.prerelease) -and $_.tag_name -match '^v\d+\.\d+\.\d+(\.\d+)?$'
    } | Sort-Object { [version]$_.tag_name.Substring(1) } -Descending)
    foreach ($release in $compatible) {
        $asset = @($release.assets | Where-Object name -EQ 'AutoAimmy-update-win-x64.zip')
        $manifest = @($release.assets | Where-Object name -EQ 'manifest.json')
        if ($asset.Count -eq 1 -and $manifest.Count -eq 1) {
            return [pscustomobject]@{ Version = $release.tag_name.Substring(1); Asset = $asset[0]; Manifest = $manifest[0] }
        }
    }
    return $null
}

function Save-GitHubAsset {
    param([string]$Root, $Asset, [string]$Destination)
    $uri = [uri]$Asset.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'api.github.com' -or $uri.AbsolutePath -notmatch '^/repos/[^/]+/[^/]+/releases/assets/\d+$') {
        throw 'Untrusted asset URL.'
    }
    Invoke-WebRequest -UseBasicParsing -Uri $uri -Headers (Get-UpdateHeaders $Root 'application/octet-stream') -OutFile $Destination -TimeoutSec 300
}

function Install-UpdateArchive {
    param([string]$Root, [string]$Archive, $Manifest)
    Assert-AppStopped $Root
    $version = [string]$Manifest.Version
    $null = Get-SafeVersion $version
    if ($Manifest.Schema -ne 1 -or $Manifest.Product -ne 'AutoAimmy' -or $Manifest.Runtime -ne 'win-x64') { throw 'Incompatible package.' }
    if ($Manifest.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Manifest.Sha256) {
        throw 'Package checksum mismatch. Current version retained.'
    }
    $state = Get-InstalledState $Root
    if ((Get-SafeVersion $version) -le (Get-SafeVersion $state.Current)) { return $false }
    $versions = Join-Path $Root 'versions'
    $destination = Join-Path $versions $version
    if (Test-Path -LiteralPath $destination) { throw 'Version directory already exists. Nothing replaced.' }
    $staging = Join-Path $Root ('.staging\' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $staging -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $expandedBytes = 0L
        foreach ($entry in $zip.Entries) {
            $expandedBytes += $entry.Length
            if ($expandedBytes -gt 2GB) { throw 'Package exceeds extraction limit.' }
            $relative = $entry.FullName.Replace('/', '\')
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or ($relative.Split('\') -contains '..')) { throw 'Unsafe ZIP entry.' }
            $target = [IO.Path]::GetFullPath((Join-Path $staging $relative))
            if (!$target.StartsWith(([IO.Path]::GetFullPath($staging).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe extraction path.' }
            if (!$entry.Name) { $null = New-Item -ItemType Directory -Path $target -Force; continue }
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $zip.Dispose() }
    foreach ($file in @('YmmiaV2.exe', 'YmmiaV2.dll', 'release.json', 'Client.ps1', 'Updater.psm1')) {
        if (!(Test-Path -LiteralPath (Join-Path $staging $file) -PathType Leaf)) { throw "Incomplete package: $file" }
    }
    $releaseInfo = Get-Content -LiteralPath (Join-Path $staging 'release.json') -Raw | ConvertFrom-Json
    if ($releaseInfo.Version -ne $version -or $releaseInfo.Product -ne 'AutoAimmy') { throw 'Version mismatch.' }
    $null = New-Item -ItemType Directory -Path $versions -Force
    [IO.Directory]::Move($staging, $destination)
    Write-State $Root ([ordered]@{ Current = $version; Previous = $state.Current })
    return $true
}

function Invoke-UpdateCheck {
    param([string]$Root, [switch]$AcceptUpdate)
    $update = Get-RemoteUpdate $Root
    if (!$update) { Write-Host 'No compatible release available.'; return }
    $state = Get-InstalledState $Root
    if ((Get-SafeVersion $update.Version) -le (Get-SafeVersion $state.Current)) { Write-Host 'AutoAimmy is up to date.'; return }
    Write-Host "New version: $($update.Version) (installed: $($state.Current))."
    if (!$AcceptUpdate -and (Read-Host 'Install now? [y/N]') -notmatch '^(y|yes|o|oui)$') { return }
    Assert-AppStopped $Root
    $cache = Join-Path $Root ('.cache\' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $cache -Force
    $manifestPath = Join-Path $cache 'manifest.json'
    Save-GitHubAsset $Root $update.Manifest $manifestPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Version -ne $update.Version) { throw 'Release manifest mismatch.' }
    $archive = Join-Path $cache 'update.zip'
    Save-GitHubAsset $Root $update.Asset $archive
    if (Install-UpdateArchive $Root $archive $manifest) { Write-Host "Installed $($manifest.Version). Previous version retained." }
}

function Invoke-Rollback {
    param([string]$Root)
    Assert-AppStopped $Root
    $state = Get-InstalledState $Root
    if (!$state.Previous) { throw 'No previous version available.' }
    $previousPath = Join-Path $Root ('versions\' + $state.Previous + '\YmmiaV2.exe')
    if (!(Test-Path -LiteralPath $previousPath)) { throw 'Previous version is incomplete.' }
    Write-State $Root ([ordered]@{ Current = $state.Previous; Previous = $state.Current })
    Write-Host "Restored $($state.Previous). Personal data retained."
}

function Export-TestReport {
    param([string]$Root)
    $sessions = Join-Path $Root 'data\sessions'
    $session = Get-ChildItem -LiteralPath $sessions -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'analysis.json') } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (!$session) { throw 'No completed session. Close the recording session first.' }
    $exports = Join-Path $Root 'exports'
    $null = New-Item -ItemType Directory -Path $exports -Force
    $destination = Join-Path $exports ('AutoAimmy-report-' + $session.Name + '.zip')
    if (Test-Path -LiteralPath $destination) { $destination = Join-Path $exports ('AutoAimmy-report-' + [guid]::NewGuid().ToString('N') + '.zip') }
    $files = @('analysis.json', 'analysis.txt', 'quality.json', 'engagements.jsonl') | ForEach-Object { Join-Path $session.FullName $_ } | Where-Object { Test-Path -LiteralPath $_ }
    Compress-Archive -LiteralPath $files -DestinationPath $destination
    Write-Host "Report exported: $destination"
    Write-Host 'No raw cursor events, GitHub credentials or configuration included. Send this ZIP manually.'
    return $destination
}

Export-ModuleMember -Function Get-SafeVersion, Get-InstalledState, Install-UpdateArchive, Invoke-UpdateCheck, Invoke-Rollback, Export-TestReport, Assert-AppStopped
