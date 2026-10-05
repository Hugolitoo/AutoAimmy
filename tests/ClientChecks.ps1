$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$root = Join-Path $env:TEMP ('AutoAimmy-client-checks-' + [guid]::NewGuid().ToString('N'))
$version = Join-Path $root 'versions\0.1.5'
$null = New-Item -ItemType Directory -Path $version -Force
foreach ($file in @('Client.ps1','Updater.psm1','PlayerProfiles.psm1','AutomaticSettings.psm1')) { Copy-Item (Join-Path $project ('distribution\' + $file)) $version }
@{Current='0.1.5';Previous=$null} | ConvertTo-Json | Set-Content (Join-Path $root 'current.json')
@{Repository='';Channel='test'} | ConvertTo-Json | Set-Content (Join-Path $root 'updater.json')
$client = Join-Path $version 'Client.ps1'
& $client -Root $root -Action Update
if (!(Test-Path (Join-Path $root 'Profil-joueur.cmd'))) { throw 'Update-only launch did not install shortcut' }
$first = Get-Content (Join-Path $root 'Profil-joueur.cmd') -Raw
& $client -Root $root -Action Check
if ((Get-Content (Join-Path $root 'Profil-joueur.cmd') -Raw) -ne $first) { throw 'Shortcut repair is not idempotent' }
if ((Get-Content (Join-Path $root 'current.json') -Raw | ConvertFrom-Json).Current -ne '0.1.5') { throw 'Shortcut installation modified current version' }
Write-Host 'PASS: updater-only shortcut installation and idempotent launcher repair.'
