$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'distribution\Updater.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$checks = 0
function Assert-Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAILED: $Name" }
    $script:checks++
}
function Assert-Throws([scriptblock]$Operation, [string]$Name) {
    $failed = $false
    try { & $Operation } catch { $failed = $true }
    Assert-Check $failed $Name
}
$fixture = Join-Path $env:TEMP ('AutoAimmy-update-checks-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $fixture 'install'
$null = New-Item -ItemType Directory -Path (Join-Path $root 'versions\0.1.0') -Force
$null = New-Item -ItemType Directory -Path (Join-Path $root 'data\sessions') -Force
Set-Content -LiteralPath (Join-Path $root 'data\player.txt') -Value 'KEEP PLAYER DATA'
Set-Content -LiteralPath (Join-Path $root 'versions\0.1.0\YmmiaV2.exe') -Value 'fixture'
@{Current='0.1.0'; Previous=$null} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'current.json')
@{Repository='Hugolitoo/AutoAimmy';Channel='test'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'updater.json')
$selection = & (Get-Module Updater) {
    param($fixtureRoot)
    function script:Invoke-RestMethod {
        param($Uri, $Headers, $TimeoutSec)
        # Match Invoke-RestMethod: one pipeline result containing a JSON array.
        return , @(
            [pscustomobject]@{draft=$false;prerelease=$true;tag_name='v0.1.2';assets=@([pscustomobject]@{name='AutoAimmy-update-win-x64.zip'},[pscustomobject]@{name='manifest.json'})},
            [pscustomobject]@{draft=$false;prerelease=$false;tag_name='v0.1.1';assets=@([pscustomobject]@{name='AutoAimmy-update-win-x64.zip'},[pscustomobject]@{name='manifest.json'})},
            [pscustomobject]@{draft=$true;prerelease=$false;tag_name='v9.0.0';assets=@()}
        )
    }
    try {
        $test = Get-RemoteUpdate $fixtureRoot
        @{Repository='Hugolitoo/AutoAimmy';Channel='stable'} | ConvertTo-Json | Set-Content (Join-Path $fixtureRoot 'updater.json')
        $stable = Get-RemoteUpdate $fixtureRoot
        [pscustomobject]@{Test=$test.Version;Stable=$stable.Version}
    } finally { Remove-Item -LiteralPath Function:script:Invoke-RestMethod }
} $root
Assert-Check ($selection.Test -eq '0.1.2') 'multiple release JSON array selects latest prerelease'
Assert-Check ($selection.Stable -eq '0.1.1') 'multiple release JSON array filters drafts and prereleases'
$payload = Join-Path $fixture 'payload'
$null = New-Item -ItemType Directory -Path $payload
foreach ($file in @('YmmiaV2.exe','YmmiaV2.dll','Client.ps1','Updater.psm1')) { Set-Content -LiteralPath (Join-Path $payload $file) -Value 'fixture only - never execute' }
@{Product='AutoAimmy'; Version='0.1.1'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'release.json')
$archive = Join-Path $fixture 'update.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
$manifest = [pscustomobject]@{Schema=1; Product='AutoAimmy'; Runtime='win-x64'; Version='0.1.1'; Sha256=(Get-FileHash $archive).Hash}
$badManifest = [pscustomobject]@{Schema=1; Product='AutoAimmy'; Runtime='win-x64'; Version='0.1.1'; Sha256=('0' * 64)}
Assert-Throws { Install-UpdateArchive $root $archive $badManifest } 'reject corrupt checksum'
Assert-Check ((Get-InstalledState $root).Current -eq '0.1.0') 'failure preserves pointer'
Assert-Check (Install-UpdateArchive $root $archive $manifest) 'install newer release'
$state = Get-InstalledState $root
Assert-Check ($state.Current -eq '0.1.1' -and $state.Previous -eq '0.1.0') 'current and previous retained'
Assert-Check ((Get-Content -LiteralPath (Join-Path $root 'data\player.txt')) -eq 'KEEP PLAYER DATA') 'preserve player data'
Assert-Check (!(Install-UpdateArchive $root $archive $manifest)) 'same version ignored'
Invoke-Rollback $root
Assert-Check ((Get-InstalledState $root).Current -eq '0.1.0') 'rollback swaps versions'
Assert-Check (Test-Path -LiteralPath (Join-Path $root 'versions\0.1.1\YmmiaV2.exe')) 'rollback keeps new version'
Assert-Throws { Get-SafeVersion '../../escape' } 'reject version traversal'

$evil = Join-Path $fixture 'evil.zip'
$zip = [IO.Compression.ZipFile]::Open($evil, [IO.Compression.ZipArchiveMode]::Create)
$entry = $zip.CreateEntry('../outside.txt')
$writer = New-Object IO.StreamWriter($entry.Open())
$writer.Write('unsafe'); $writer.Dispose(); $zip.Dispose()
$evilManifest = [pscustomobject]@{Schema=1; Product='AutoAimmy'; Runtime='win-x64'; Version='0.1.2'; Sha256=(Get-FileHash $evil).Hash}
Assert-Throws { Install-UpdateArchive $root $evil $evilManifest } 'reject ZIP traversal'
Assert-Check (!(Test-Path -LiteralPath (Join-Path $root 'outside.txt'))) 'no extraction outside staging'
Assert-Check ((Get-InstalledState $root).Current -eq '0.1.0') 'unsafe ZIP does not promote'

$session = Join-Path $root 'data\sessions\sample'
$null = New-Item -ItemType Directory -Path $session
foreach ($name in @('analysis.json','analysis.txt','quality.json','engagements.jsonl','events.jsonl')) { Set-Content -LiteralPath (Join-Path $session $name) -Value '{}' }
Set-Content -LiteralPath (Join-Path $root 'data\github-access.xml') -Value 'not a real credential'
$report = Export-TestReport $root
$zip = [IO.Compression.ZipFile]::OpenRead($report)
try {
    Assert-Check ($zip.Entries.Count -eq 4) 'export selected report files'
    Assert-Check (@($zip.Entries | Where-Object { $_.Name -in @('events.jsonl','github-access.xml') }).Count -eq 0) 'exclude raw events and credentials'
} finally { $zip.Dispose() }
Write-Host "PASS: $checks updater checks. Fixtures preserved at $fixture"
